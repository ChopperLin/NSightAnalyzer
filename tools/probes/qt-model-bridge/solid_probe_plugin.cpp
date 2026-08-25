#include <QAbstractButton>
#include <QAbstractItemView>
#include <QAbstractProxyModel>
#include <QAction>
#include <QApplication>
#include <QComboBox>
#include <QCoreApplication>
#include <QDateTime>
#include <QDir>
#include <QEvent>
#include <QFile>
#include <QFileDialog>
#include <QFileInfo>
#include <QGenericPlugin>
#include <QItemSelectionModel>
#include <QJsonArray>
#include <QJsonDocument>
#include <QJsonObject>
#include <QMetaMethod>
#include <QMetaProperty>
#include <QMouseEvent>
#include <QPointer>
#include <QSaveFile>
#include <QSet>
#include <QStackedWidget>
#include <QTableView>
#include <QTabWidget>
#include <QThread>
#include <QTimer>
#include <QTreeView>
#include <QtPlugin>

#include <algorithm>

namespace {

constexpr auto kPluginVersion = "probe-0.45";
constexpr auto kVerifiedNsightVersion = "2026.2.0";
constexpr auto kVerifiedNsightBuild = "37991608";
constexpr auto kSessionSchema = "NsightSolidProbeSessionV1";
constexpr auto kSessionRequestSchema = "NsightSolidProbeSessionRequestV1";

QJsonValue VariantToJson(const QVariant& value)
{
    if (!value.isValid() || value.isNull()) {
        return QJsonValue();
    }

    const QJsonValue jsonValue = QJsonValue::fromVariant(value);
    return jsonValue.isUndefined() ? QJsonValue(value.toString()) : jsonValue;
}

int EnvironmentInt(const QString& name)
{
    const QByteArray key = name.toUtf8();
    return qEnvironmentVariableIntValue(key.constData());
}

struct ExportOptions
{
    int offset = 0;
    int limit = 25000;
    int maxDepth = 24;
    bool includeItemData = false;
    bool includeHeaders = true;
    bool flat = false;
    QList<int> columns;
    QString valueMode = "normal";
};

ExportOptions ReadExportOptions(const QString& scope, bool defaultItemData = false)
{
    ExportOptions options;
    const QString prefix = "NSIGHT_SOLID_PROBE_" + scope;
    const int scopedOffset = EnvironmentInt(prefix + "_OFFSET");
    const int scopedLimit = EnvironmentInt(prefix + "_LIMIT");
    const int legacyLimit = qEnvironmentVariableIntValue("NSIGHT_SOLID_PROBE_MAX_NODES");
    const int configuredMaxDepth = qEnvironmentVariableIntValue("NSIGHT_SOLID_PROBE_MAX_DEPTH");

    options.offset = qMax(0, scopedOffset);
    if (scopedLimit > 0) {
        options.limit = qBound(1, scopedLimit, 250000);
    } else if (legacyLimit > 0) {
        options.limit = qBound(1, legacyLimit, 250000);
    }
    if (configuredMaxDepth > 0) {
        options.maxDepth = qBound(1, configuredMaxDepth, 64);
    }
    options.includeItemData = defaultItemData
        || EnvironmentInt(prefix + "_INCLUDE_ITEM_DATA") == 1;
    const QByteArray includeHeadersKey = (prefix + "_INCLUDE_HEADERS").toUtf8();
    if (qEnvironmentVariableIsSet(includeHeadersKey.constData())) {
        options.includeHeaders = EnvironmentInt(prefix + "_INCLUDE_HEADERS") == 1;
    }
    options.flat = EnvironmentInt(prefix + "_FLAT") == 1;
    QString configuredColumns = qEnvironmentVariable(
        (prefix + "_COLUMNS").toUtf8().constData()).trimmed();
    configuredColumns.replace(';', ',');
    for (const QString& value : configuredColumns.split(',', Qt::SkipEmptyParts)) {
        bool ok = false;
        const int column = value.trimmed().toInt(&ok);
        if (ok && column >= 0 && !options.columns.contains(column)) {
            options.columns.append(column);
        }
    }
    std::sort(options.columns.begin(), options.columns.end());
    const QString valueMode = qEnvironmentVariable(
        (prefix + "_VALUE_MODE").toUtf8().constData()).trimmed().toLower();
    if (valueMode == "type" || valueMode == "string") {
        options.valueMode = valueMode;
    }
    return options;
}

QList<int> SelectedColumns(int totalColumns, const ExportOptions& options)
{
    QList<int> selected;
    if (options.columns.isEmpty()) {
        for (int column = 0; column < totalColumns; ++column) {
            selected.append(column);
        }
        return selected;
    }
    for (const int column : options.columns) {
        if (column < totalColumns) {
            selected.append(column);
        }
    }
    return selected;
}

QJsonArray ColumnsToJson(const QList<int>& columns)
{
    QJsonArray values;
    for (const int column : columns) {
        values.append(column);
    }
    return values;
}

QString VariantTypeName(const QVariant& value)
{
    const char* typeName = value.metaType().name();
    return typeName == nullptr ? QString() : QString::fromLatin1(typeName);
}

void InsertVariantValue(
    QJsonObject& object,
    const QString& name,
    const QVariant& value,
    const QString& valueMode)
{
    object.insert(name + "Type", VariantTypeName(value));
    if (valueMode == "type") {
        return;
    }
    object.insert(name, valueMode == "string"
        ? QJsonValue(value.toString())
        : VariantToJson(value));
}

struct ExportState
{
    ExportOptions options;
    QJsonArray nodes;
    int totalCount = 0;
    int returnedCount = 0;
    bool totalCountExact = true;
};

QString StandardRoleName(int role)
{
    switch (role) {
    case Qt::DisplayRole: return "display";
    case Qt::DecorationRole: return "decoration";
    case Qt::EditRole: return "edit";
    case Qt::ToolTipRole: return "toolTip";
    case Qt::StatusTipRole: return "statusTip";
    case Qt::WhatsThisRole: return "whatsThis";
    case Qt::FontRole: return "font";
    case Qt::TextAlignmentRole: return "textAlignment";
    case Qt::BackgroundRole: return "background";
    case Qt::ForegroundRole: return "foreground";
    case Qt::CheckStateRole: return "checkState";
    case Qt::AccessibleTextRole: return "accessibleText";
    case Qt::AccessibleDescriptionRole: return "accessibleDescription";
    case Qt::SizeHintRole: return "sizeHint";
    case Qt::InitialSortOrderRole: return "initialSortOrder";
    default: return {};
    }
}

QJsonArray ExportItemData(QAbstractItemModel* model, const QModelIndex& index)
{
    QJsonArray roles;
    const QHash<int, QByteArray> roleNames = model->roleNames();
    const QMap<int, QVariant> itemData = model->itemData(index);
    for (auto iterator = itemData.cbegin(); iterator != itemData.cend(); ++iterator) {
        QString roleName = QString::fromLatin1(roleNames.value(iterator.key()));
        if (roleName.isEmpty()) {
            roleName = StandardRoleName(iterator.key());
        }
        const char* typeName = iterator.value().metaType().name();
        roles.append(QJsonObject{
            {"id", iterator.key()},
            {"name", roleName},
            {"type", typeName == nullptr ? QString() : QString::fromLatin1(typeName)},
            {"value", VariantToJson(iterator.value())},
        });
    }
    return roles;
}

void AppendRows(
    QAbstractItemModel* model,
    const QModelIndex& parent,
    int depth,
    const QJsonArray& parentPath,
    ExportState& state)
{
    if (model == nullptr || depth >= state.options.maxDepth) {
        if (model != nullptr && model->rowCount(parent) > 0) {
            state.totalCountExact = false;
        }
        return;
    }

    const int rows = model->rowCount(parent);
    const int columns = model->columnCount(parent);
    for (int row = 0; row < rows; ++row) {
        const QModelIndex treeIndex = model->index(row, 0, parent);
        if (!treeIndex.isValid()) {
            continue;
        }

        QJsonArray path = parentPath;
        path.append(row);

        const int ordinal = state.totalCount++;
        const bool includeNode = ordinal >= state.options.offset
            && state.returnedCount < state.options.limit;

        const int childCount = state.options.flat ? 0 : model->rowCount(treeIndex);
        if (includeNode) {
            QJsonArray cells;
            for (const int column : SelectedColumns(columns, state.options)) {
                const QModelIndex cellIndex = model->index(row, column, parent);
                const QVariant display = model->data(cellIndex, Qt::DisplayRole);

                QJsonObject cell{
                    {"column", column},
                };
                InsertVariantValue(
                    cell, "display", display, state.options.valueMode);
                if (state.options.valueMode != "type") {
                    const QVariant tooltip = model->data(cellIndex, Qt::ToolTipRole);
                    if (tooltip.isValid() && tooltip != display) {
                        InsertVariantValue(
                            cell, "tooltip", tooltip, state.options.valueMode);
                    }
                }
                if (state.options.includeItemData && state.options.valueMode == "normal") {
                    cell.insert("roles", ExportItemData(model, cellIndex));
                }
                cells.append(cell);
            }

            state.nodes.append(QJsonObject{
                {"ordinal", ordinal},
                {"path", path},
                {"depth", depth},
                {"row", row},
                {"childCount", childCount},
                {"cells", cells},
            });
            ++state.returnedCount;
        }

        if (!state.options.flat && childCount > 0) {
            AppendRows(model, treeIndex, depth + 1, path, state);
        }
    }
}

QJsonObject ExportModel(QAbstractItemModel* model, const ExportOptions& options)
{
    ExportState state{options};

    QJsonArray headers;
    const int rootRows = model->rowCount();
    const int columns = model->columnCount();
    const QList<int> selectedColumns = SelectedColumns(columns, options);
    if (rootRows == 0) {
        return QJsonObject{
            {"modelClass", model->metaObject()->className()},
            {"modelObjectName", model->objectName()},
            {"rootRows", 0},
            {"rootColumns", columns},
            {"selectedColumns", ColumnsToJson(selectedColumns)},
            {"headers", headers},
            {"roleNames", QJsonArray()},
            {"nodes", state.nodes},
            {"totalCount", 0},
            {"totalCountExact", true},
            {"offset", state.options.offset},
            {"limit", state.options.limit},
            {"returnedCount", 0},
            {"nodeCount", 0},
            {"maxNodes", state.options.limit},
            {"hasMore", false},
            {"maxDepth", state.options.maxDepth},
            {"includeItemData", state.options.includeItemData},
            {"includeHeaders", state.options.includeHeaders},
            {"flat", state.options.flat},
            {"valueMode", state.options.valueMode},
            {"emptyModel", true},
            {"truncated", false},
        };
    }
    if (options.includeHeaders) {
        for (const int column : selectedColumns) {
            QJsonObject header{{"column", column}};
            InsertVariantValue(
                header,
                "display",
                model->headerData(column, Qt::Horizontal, Qt::DisplayRole),
                options.valueMode);
            headers.append(header);
        }
    }

    QJsonArray roles;
    if (options.includeItemData) {
        const QHash<int, QByteArray> roleNames = model->roleNames();
        QList<int> roleIds = roleNames.keys();
        std::sort(roleIds.begin(), roleIds.end());
        for (const int roleId : roleIds) {
            roles.append(QJsonObject{
                {"id", roleId},
                {"name", QString::fromLatin1(roleNames.value(roleId))},
            });
        }
    }

    AppendRows(model, QModelIndex(), 0, QJsonArray(), state);
    const bool hasMore = !state.totalCountExact
        || state.totalCount > state.options.offset + state.returnedCount;
    return QJsonObject{
        {"modelClass", model->metaObject()->className()},
        {"modelObjectName", model->objectName()},
        {"rootRows", rootRows},
        {"rootColumns", columns},
        {"selectedColumns", ColumnsToJson(selectedColumns)},
        {"headers", headers},
        {"roleNames", roles},
        {"nodes", state.nodes},
        {"totalCount", state.totalCount},
        {"totalCountExact", state.totalCountExact},
        {"offset", state.options.offset},
        {"limit", state.options.limit},
        {"returnedCount", state.returnedCount},
        {"nodeCount", state.returnedCount},
        {"maxNodes", state.options.limit},
        {"hasMore", hasMore},
        {"maxDepth", state.options.maxDepth},
        {"includeItemData", state.options.includeItemData},
        {"includeHeaders", state.options.includeHeaders},
        {"flat", state.options.flat},
        {"valueMode", state.options.valueMode},
        {"truncated", hasMore},
    };
}

QAbstractItemModel* UnwrapProxyModel(QAbstractItemModel* model, QJsonArray* modelChain = nullptr)
{
    QAbstractItemModel* sourceModel = model;
    if (modelChain != nullptr && sourceModel != nullptr) {
        modelChain->append(sourceModel->metaObject()->className());
    }
    while (auto* proxy = qobject_cast<QAbstractProxyModel*>(sourceModel)) {
        if (proxy->sourceModel() == nullptr || proxy->sourceModel() == sourceModel) {
            break;
        }
        sourceModel = proxy->sourceModel();
        if (modelChain != nullptr) {
            modelChain->append(sourceModel->metaObject()->className());
        }
    }
    return sourceModel;
}

QJsonArray ObjectAncestry(const QObject* object)
{
    QJsonArray ancestry;
    const QObject* cursor = object == nullptr ? nullptr : object->parent();
    for (int depth = 0; cursor != nullptr && depth < 12; ++depth) {
        ancestry.append(QJsonObject{
            {"class", cursor->metaObject()->className()},
            {"objectName", cursor->objectName()},
        });
        cursor = cursor->parent();
    }
    return ancestry;
}

QStringList MetricTableFilters()
{
    QStringList filters = qEnvironmentVariable("NSIGHT_SOLID_PROBE_METRIC_TABLE_MATCH")
        .split(';', Qt::SkipEmptyParts);
    for (QString& filter : filters) {
        filter = filter.trimmed();
    }
    filters.removeAll(QString());
    return filters;
}

bool MetricTableMatches(const QString& tableName, const QStringList& filters)
{
    if (filters.isEmpty()) {
        return true;
    }
    const bool exact = qEnvironmentVariable("NSIGHT_SOLID_PROBE_METRIC_TABLE_MATCH_MODE")
        .compare("exact", Qt::CaseInsensitive) == 0;
    for (const QString& filter : filters) {
        if ((exact && tableName.compare(filter, Qt::CaseInsensitive) == 0)
            || (!exact && tableName.contains(filter, Qt::CaseInsensitive))) {
            return true;
        }
    }
    return false;
}

QJsonArray CollectMetricViews(
    QApplication* application,
    bool includeData,
    bool* ready,
    int* totalCount = nullptr)
{
    QJsonArray matches;
    *ready = false;
    int discoveredCount = 0;
    QHash<QString, int> instanceOrdinals;
    const QStringList tableFilters = MetricTableFilters();
    const auto widgets = application->allWidgets();
    for (QWidget* widget : widgets) {
        auto* view = qobject_cast<QAbstractItemView*>(widget);
        if (view == nullptr || view->model() == nullptr) {
            continue;
        }

        const QString viewClass = QString::fromLatin1(widget->metaObject()->className());
        if (viewClass != "NV::WarpViz::WarpMetricsTableView") {
            continue;
        }

        ++discoveredCount;
        const QString tableName = widget->objectName();
        const int instanceOrdinal = instanceOrdinals.value(tableName, 0);
        instanceOrdinals.insert(tableName, instanceOrdinal + 1);

        QAbstractItemModel* model = view->model();
        QJsonArray modelChain;
        QAbstractItemModel* sourceModel = UnwrapProxyModel(model, &modelChain);
        const int rows = model->rowCount();
        const int columns = model->columnCount();
        *ready = *ready || (sourceModel->rowCount() > 0 && sourceModel->columnCount() > 0);
        if (!MetricTableMatches(tableName, tableFilters)) {
            continue;
        }

        QJsonObject match{
            {"viewClass", viewClass},
            {"viewObjectName", tableName},
            {"instanceOrdinal", instanceOrdinal},
            {"visible", widget->isVisible()},
            {"ancestry", ObjectAncestry(widget)},
            {"modelClass", model->metaObject()->className()},
            {"modelObjectName", model->objectName()},
            {"modelChain", modelChain},
            {"sourceRows", sourceModel->rowCount()},
            {"sourceColumns", sourceModel->columnCount()},
            {"rows", rows},
            {"columns", columns},
        };
        if (includeData) {
            match.insert("export", ExportModel(model, ReadExportOptions("METRIC")));
        }
        matches.append(match);
    }
    if (totalCount != nullptr) {
        *totalCount = discoveredCount;
    }
    return matches;
}

QList<QAbstractItemModel*> DiscoverModels(QApplication* application)
{
    QSet<QAbstractItemModel*> uniqueModels;
    const auto addModels = [&uniqueModels](const QList<QAbstractItemModel*>& models) {
        for (QAbstractItemModel* model : models) {
            if (model != nullptr) {
                uniqueModels.insert(model);
            }
        }
    };

    addModels(application->findChildren<QAbstractItemModel*>());
    const auto topLevels = application->topLevelWidgets();
    for (QWidget* topLevel : topLevels) {
        addModels(topLevel->findChildren<QAbstractItemModel*>());
    }

    const auto widgets = application->allWidgets();
    for (QWidget* widget : widgets) {
        auto* view = qobject_cast<QAbstractItemView*>(widget);
        if (view == nullptr || view->model() == nullptr) {
            continue;
        }
        QAbstractItemModel* model = view->model();
        while (model != nullptr && !uniqueModels.contains(model)) {
            uniqueModels.insert(model);
            auto* proxy = qobject_cast<QAbstractProxyModel*>(model);
            model = proxy == nullptr ? nullptr : proxy->sourceModel();
        }
    }

    QList<QAbstractItemModel*> models = uniqueModels.values();
    std::sort(models.begin(), models.end(), [](const auto* left, const auto* right) {
        const QString leftKey = QString::fromLatin1(left->metaObject()->className())
            + "\n" + left->objectName();
        const QString rightKey = QString::fromLatin1(right->metaObject()->className())
            + "\n" + right->objectName();
        if (leftKey != rightKey) {
            return leftKey < rightKey;
        }
        return left < right;
    });
    return models;
}

QHash<QAbstractItemModel*, QJsonArray> BuildModelViewAttachments(QApplication* application)
{
    QHash<QAbstractItemModel*, QJsonArray> attachments;
    const auto widgets = application->allWidgets();
    for (QWidget* widget : widgets) {
        auto* view = qobject_cast<QAbstractItemView*>(widget);
        if (view == nullptr || view->model() == nullptr) {
            continue;
        }

        QJsonObject attachment{
            {"viewClass", widget->metaObject()->className()},
            {"viewObjectName", widget->objectName()},
            {"visible", widget->isVisible()},
            {"ancestry", ObjectAncestry(widget)},
        };
        QWidget* ancestor = widget->parentWidget();
        while (ancestor != nullptr) {
            if (auto* comboBox = qobject_cast<QComboBox*>(ancestor)) {
                attachment.insert("comboClass", comboBox->metaObject()->className());
                attachment.insert("comboObjectName", comboBox->objectName());
                attachment.insert("comboCurrentIndex", comboBox->currentIndex());
                attachment.insert("comboCurrentText", comboBox->currentText());
                attachment.insert("comboCount", comboBox->count());
                break;
            }
            ancestor = ancestor->parentWidget();
        }
        if (auto* treeView = qobject_cast<QTreeView*>(view)) {
            QJsonArray visibleColumns;
            QJsonArray hiddenColumns;
            const QModelIndex viewRoot = treeView->rootIndex();
            const int columns = treeView->model()->columnCount(viewRoot);
            for (int column = 0; column < columns; ++column) {
                (treeView->isColumnHidden(column) ? hiddenColumns : visibleColumns)
                    .append(column);
            }
            QJsonArray visibleRows;
            QJsonArray hiddenRows;
            const int rows = treeView->model()->rowCount(viewRoot);
            for (int row = 0; row < rows; ++row) {
                (treeView->isRowHidden(row, viewRoot) ? hiddenRows : visibleRows)
                    .append(row);
            }
            QJsonArray rootPath;
            QModelIndex root = viewRoot;
            while (root.isValid()) {
                rootPath.prepend(root.row());
                root = root.parent();
            }
            attachment.insert("visibleColumns", visibleColumns);
            attachment.insert("hiddenColumns", hiddenColumns);
            attachment.insert("viewModelClass", treeView->model()->metaObject()->className());
            attachment.insert("viewRootColumns", columns);
            attachment.insert("visibleRootRows", visibleRows);
            attachment.insert("hiddenRootRows", hiddenRows);
            attachment.insert("rootPath", rootPath);
        } else if (auto* tableView = qobject_cast<QTableView*>(view)) {
            QJsonArray visibleColumns;
            QJsonArray hiddenColumns;
            const QModelIndex viewRoot = tableView->rootIndex();
            const int columns = tableView->model()->columnCount(viewRoot);
            for (int column = 0; column < columns; ++column) {
                (tableView->isColumnHidden(column) ? hiddenColumns : visibleColumns)
                    .append(column);
            }
            QJsonArray visibleRows;
            QJsonArray hiddenRows;
            const int rows = tableView->model()->rowCount(viewRoot);
            for (int row = 0; row < rows; ++row) {
                (tableView->isRowHidden(row) ? hiddenRows : visibleRows).append(row);
            }
            attachment.insert("visibleColumns", visibleColumns);
            attachment.insert("hiddenColumns", hiddenColumns);
            attachment.insert("viewModelClass", tableView->model()->metaObject()->className());
            attachment.insert("viewRootColumns", columns);
            attachment.insert("visibleRootRows", visibleRows);
            attachment.insert("hiddenRootRows", hiddenRows);
        }
        QAbstractItemModel* model = view->model();
        QSet<QAbstractItemModel*> chain;
        while (model != nullptr && !chain.contains(model)) {
            chain.insert(model);
            QJsonArray modelAttachments = attachments.value(model);
            modelAttachments.append(attachment);
            attachments.insert(model, modelAttachments);
            auto* proxy = qobject_cast<QAbstractProxyModel*>(model);
            model = proxy == nullptr ? nullptr : proxy->sourceModel();
        }
    }
    return attachments;
}

bool TextFilterMatches(const QString& value, const QStringList& filters, bool exact)
{
    if (filters.isEmpty()) {
        return true;
    }
    for (const QString& filter : filters) {
        if ((exact && value.compare(filter, Qt::CaseInsensitive) == 0)
            || (!exact && value.contains(filter, Qt::CaseInsensitive))) {
            return true;
        }
    }
    return false;
}

QStringList EnvironmentFilters(const char* name)
{
    QStringList filters = qEnvironmentVariable(name).split(';', Qt::SkipEmptyParts);
    for (QString& filter : filters) {
        filter = filter.trimmed();
    }
    filters.removeAll(QString());
    return filters;
}

bool ObjectChainMatches(
    const QObject* object,
    const QStringList& classFilters,
    const QStringList& objectFilters,
    bool exact)
{
    bool classMatched = classFilters.isEmpty();
    bool objectMatched = objectFilters.isEmpty();
    const QObject* cursor = object;
    for (int depth = 0; cursor != nullptr && depth < 16; ++depth) {
        classMatched = classMatched || TextFilterMatches(
            QString::fromLatin1(cursor->metaObject()->className()), classFilters, exact);
        objectMatched = objectMatched || TextFilterMatches(
            cursor->objectName(), objectFilters, exact);
        cursor = cursor->parent();
    }
    return classMatched && objectMatched;
}

QList<QAction*> DiscoverActions(QApplication* application)
{
    const QStringList classFilters = EnvironmentFilters(
        "NSIGHT_SOLID_PROBE_ACTION_CLASS_MATCH");
    const QStringList objectFilters = EnvironmentFilters(
        "NSIGHT_SOLID_PROBE_ACTION_OBJECT_MATCH");
    const QStringList textFilters = EnvironmentFilters(
        "NSIGHT_SOLID_PROBE_ACTION_TRIGGER_TEXT_MATCH");
    const QStringList ancestryClassFilters = EnvironmentFilters(
        "NSIGHT_SOLID_PROBE_ACTION_ANCESTRY_CLASS_MATCH");
    const QStringList ancestryObjectFilters = EnvironmentFilters(
        "NSIGHT_SOLID_PROBE_ACTION_ANCESTRY_OBJECT_MATCH");
    const bool exact = qEnvironmentVariable("NSIGHT_SOLID_PROBE_ACTION_MATCH_MODE")
        .compare("exact", Qt::CaseInsensitive) == 0;

    QSet<QAction*> unique;
    const auto addActions = [&unique](const QList<QAction*>& actions) {
        for (QAction* action : actions) {
            if (action != nullptr) {
                unique.insert(action);
            }
        }
    };
    addActions(application->findChildren<QAction*>());
    for (QWidget* topLevel : application->topLevelWidgets()) {
        addActions(topLevel->findChildren<QAction*>());
    }

    QList<QAction*> result;
    for (QAction* action : unique) {
        const QString className = QString::fromLatin1(
            action->metaObject()->className());
        if (!TextFilterMatches(className, classFilters, exact)
            || !TextFilterMatches(action->objectName(), objectFilters, exact)
            || !TextFilterMatches(action->text(), textFilters, exact)
            || !ObjectChainMatches(
                action,
                ancestryClassFilters,
                ancestryObjectFilters,
                exact)) {
            continue;
        }
        result.append(action);
    }
    std::sort(result.begin(), result.end(), [](const auto* left, const auto* right) {
        const QString leftKey = QString::fromLatin1(left->metaObject()->className())
            + "\n" + left->objectName() + "\n" + left->text();
        const QString rightKey = QString::fromLatin1(right->metaObject()->className())
            + "\n" + right->objectName() + "\n" + right->text();
        if (leftKey != rightKey) {
            return leftKey < rightKey;
        }
        return left < right;
    });
    return result;
}

QJsonObject ActionSummary(QAction* action)
{
    if (action == nullptr) {
        return {};
    }
    return QJsonObject{
        {"class", action->metaObject()->className()},
        {"objectName", action->objectName()},
        {"text", action->text()},
        {"toolTip", action->toolTip()},
        {"enabled", action->isEnabled()},
        {"visible", action->isVisible()},
        {"checkable", action->isCheckable()},
        {"checked", action->isChecked()},
        {"ancestry", ObjectAncestry(action)},
    };
}

QList<QComboBox*> DiscoverComboBoxes(QApplication* application)
{
    const QStringList classFilters = EnvironmentFilters(
        "NSIGHT_SOLID_PROBE_COMBO_CLASS_MATCH");
    const QStringList objectFilters = EnvironmentFilters(
        "NSIGHT_SOLID_PROBE_COMBO_OBJECT_MATCH");
    const QStringList ancestryClassFilters = EnvironmentFilters(
        "NSIGHT_SOLID_PROBE_COMBO_ANCESTRY_CLASS_MATCH");
    const QStringList ancestryObjectFilters = EnvironmentFilters(
        "NSIGHT_SOLID_PROBE_COMBO_ANCESTRY_OBJECT_MATCH");
    const QStringList currentTextFilters = EnvironmentFilters(
        "NSIGHT_SOLID_PROBE_COMBO_CURRENT_TEXT_MATCH");
    const bool exact = qEnvironmentVariable("NSIGHT_SOLID_PROBE_COMBO_MATCH_MODE")
        .compare("exact", Qt::CaseInsensitive) == 0;
    const int minimumCount = qMax(0, qEnvironmentVariableIntValue(
        "NSIGHT_SOLID_PROBE_COMBO_MIN_COUNT"));
    const bool hasMaximumCount = qEnvironmentVariableIsSet(
        "NSIGHT_SOLID_PROBE_COMBO_MAX_COUNT");
    const int maximumCount = qMax(0, qEnvironmentVariableIntValue(
        "NSIGHT_SOLID_PROBE_COMBO_MAX_COUNT"));

    QList<QComboBox*> result;
    QSet<QComboBox*> unique;
    const auto widgets = application->allWidgets();
    for (QWidget* widget : widgets) {
        auto* comboBox = qobject_cast<QComboBox*>(widget);
        if (comboBox == nullptr || unique.contains(comboBox)) {
            continue;
        }
        unique.insert(comboBox);
        const QString className = QString::fromLatin1(
            comboBox->metaObject()->className());
        if (!TextFilterMatches(className, classFilters, exact)
            || !TextFilterMatches(comboBox->objectName(), objectFilters, exact)
            || !TextFilterMatches(comboBox->currentText(), currentTextFilters, exact)
            || !ObjectChainMatches(
                comboBox,
                ancestryClassFilters,
                ancestryObjectFilters,
                exact)
            || comboBox->count() < minimumCount
            || (hasMaximumCount && comboBox->count() > maximumCount)) {
            continue;
        }
        result.append(comboBox);
    }

    std::sort(result.begin(), result.end(), [](const auto* left, const auto* right) {
        const QString leftKey = QString::fromLatin1(left->metaObject()->className())
            + "\n" + left->objectName()
            + "\n" + left->currentText()
            + "\n" + QString::number(left->count());
        const QString rightKey = QString::fromLatin1(right->metaObject()->className())
            + "\n" + right->objectName()
            + "\n" + right->currentText()
            + "\n" + QString::number(right->count());
        if (leftKey != rightKey) {
            return leftKey < rightKey;
        }
        return left < right;
    });
    return result;
}

bool ComboItemMatches(QComboBox* comboBox, int index, const QString& match, bool exact)
{
    if (comboBox == nullptr || index < 0 || index >= comboBox->count()) {
        return false;
    }
    const QStringList values{
        comboBox->itemText(index),
        comboBox->itemData(index, Qt::DisplayRole).toString(),
        comboBox->itemData(index, Qt::ToolTipRole).toString(),
        comboBox->itemData(index, Qt::AccessibleTextRole).toString(),
    };
    for (const QString& value : values) {
        if ((exact && value.compare(match, Qt::CaseInsensitive) == 0)
            || (!exact && value.contains(match, Qt::CaseInsensitive))) {
            return true;
        }
    }
    return false;
}

QJsonArray CollectComboBoxes(QApplication* application, int* totalCount)
{
    const QList<QComboBox*> comboBoxes = DiscoverComboBoxes(application);
    *totalCount = comboBoxes.size();
    int itemOffset = qMax(0, qEnvironmentVariableIntValue(
        "NSIGHT_SOLID_PROBE_COMBO_ITEM_OFFSET"));
    int itemLimit = qEnvironmentVariableIntValue(
        "NSIGHT_SOLID_PROBE_COMBO_ITEM_LIMIT");
    itemLimit = itemLimit > 0 ? qBound(1, itemLimit, 10000) : 256;

    QJsonArray result;
    for (int ordinal = 0; ordinal < comboBoxes.size(); ++ordinal) {
        QComboBox* comboBox = comboBoxes.at(ordinal);
        QJsonArray items;
        const int end = qMin(comboBox->count(), itemOffset + itemLimit);
        for (int index = itemOffset; index < end; ++index) {
            QJsonObject item{
                {"index", index},
                {"text", comboBox->itemText(index)},
                {"selected", index == comboBox->currentIndex()},
            };
            const QVariant tooltip = comboBox->itemData(index, Qt::ToolTipRole);
            if (tooltip.isValid()) {
                InsertVariantValue(item, "tooltip", tooltip, "normal");
            }
            const QModelIndex modelIndex = comboBox->model() == nullptr
                ? QModelIndex()
                : comboBox->model()->index(
                    index, comboBox->modelColumn(), comboBox->rootModelIndex());
            if (modelIndex.isValid()) {
                item.insert("enabled", comboBox->model()->flags(modelIndex)
                    .testFlag(Qt::ItemIsEnabled));
            }
            items.append(item);
        }
        result.append(QJsonObject{
            {"ordinal", ordinal},
            {"class", comboBox->metaObject()->className()},
            {"objectName", comboBox->objectName()},
            {"visible", comboBox->isVisible()},
            {"enabled", comboBox->isEnabled()},
            {"currentIndex", comboBox->currentIndex()},
            {"currentText", comboBox->currentText()},
            {"count", comboBox->count()},
            {"modelClass", comboBox->model() == nullptr
                ? QString()
                : QString::fromLatin1(comboBox->model()->metaObject()->className())},
            {"modelObjectName", comboBox->model() == nullptr
                ? QString()
                : comboBox->model()->objectName()},
            {"ancestry", ObjectAncestry(comboBox)},
            {"itemOffset", itemOffset},
            {"itemLimit", itemLimit},
            {"itemReturned", items.size()},
            {"hasMoreItems", end < comboBox->count()},
            {"items", items},
        });
    }
    return result;
}

bool HasComboProbe()
{
    return qEnvironmentVariableIntValue("NSIGHT_SOLID_PROBE_COMBO_EXPORT") == 1
        || !EnvironmentFilters("NSIGHT_SOLID_PROBE_COMBO_CLASS_MATCH").isEmpty()
        || !EnvironmentFilters("NSIGHT_SOLID_PROBE_COMBO_OBJECT_MATCH").isEmpty()
        || !EnvironmentFilters("NSIGHT_SOLID_PROBE_COMBO_ANCESTRY_CLASS_MATCH").isEmpty()
        || !EnvironmentFilters("NSIGHT_SOLID_PROBE_COMBO_ANCESTRY_OBJECT_MATCH").isEmpty()
        || !EnvironmentFilters("NSIGHT_SOLID_PROBE_COMBO_CURRENT_TEXT_MATCH").isEmpty()
        || !qEnvironmentVariable("NSIGHT_SOLID_PROBE_COMBO_SELECT_MATCH").trimmed().isEmpty()
        || !qEnvironmentVariable(
            "NSIGHT_SOLID_PROBE_COMBO_SELECT_FROM_MODEL_SELECTION").trimmed().isEmpty();
}

QList<QTabWidget*> DiscoverTabWidgets(QApplication* application)
{
    const QStringList classFilters = EnvironmentFilters(
        "NSIGHT_SOLID_PROBE_TAB_CLASS_MATCH");
    const QStringList objectFilters = EnvironmentFilters(
        "NSIGHT_SOLID_PROBE_TAB_OBJECT_MATCH");
    const QStringList ancestryClassFilters = EnvironmentFilters(
        "NSIGHT_SOLID_PROBE_TAB_ANCESTRY_CLASS_MATCH");
    const QStringList ancestryObjectFilters = EnvironmentFilters(
        "NSIGHT_SOLID_PROBE_TAB_ANCESTRY_OBJECT_MATCH");
    const bool exact = qEnvironmentVariable("NSIGHT_SOLID_PROBE_TAB_MATCH_MODE")
        .compare("exact", Qt::CaseInsensitive) == 0;

    QSet<QTabWidget*> unique;
    QList<QTabWidget*> result;
    for (QWidget* widget : application->allWidgets()) {
        auto* tabWidget = qobject_cast<QTabWidget*>(widget);
        if (tabWidget == nullptr || unique.contains(tabWidget)) {
            continue;
        }
        unique.insert(tabWidget);
        const QString className = QString::fromLatin1(
            tabWidget->metaObject()->className());
        if (!TextFilterMatches(className, classFilters, exact)
            || !TextFilterMatches(tabWidget->objectName(), objectFilters, exact)
            || !ObjectChainMatches(
                tabWidget,
                ancestryClassFilters,
                ancestryObjectFilters,
                exact)) {
            continue;
        }
        result.append(tabWidget);
    }
    std::sort(result.begin(), result.end(), [](const auto* left, const auto* right) {
        const QString leftKey = QString::fromLatin1(left->metaObject()->className())
            + "\n" + left->objectName() + "\n" + QString::number(left->count());
        const QString rightKey = QString::fromLatin1(right->metaObject()->className())
            + "\n" + right->objectName() + "\n" + QString::number(right->count());
        if (leftKey != rightKey) {
            return leftKey < rightKey;
        }
        return left < right;
    });
    return result;
}

bool TabItemMatches(QTabWidget* tabWidget, int index, const QString& match, bool exact)
{
    if (tabWidget == nullptr || index < 0 || index >= tabWidget->count()) {
        return false;
    }
    const QStringList values{
        tabWidget->tabText(index),
        tabWidget->tabToolTip(index),
        tabWidget->tabWhatsThis(index),
    };
    for (const QString& value : values) {
        if ((exact && value.compare(match, Qt::CaseInsensitive) == 0)
            || (!exact && value.contains(match, Qt::CaseInsensitive))) {
            return true;
        }
    }
    return false;
}

QJsonArray CollectTabWidgets(QApplication* application, int* totalCount)
{
    const QList<QTabWidget*> tabWidgets = DiscoverTabWidgets(application);
    *totalCount = tabWidgets.size();
    int itemLimit = qEnvironmentVariableIntValue(
        "NSIGHT_SOLID_PROBE_TAB_ITEM_LIMIT");
    itemLimit = itemLimit > 0 ? qBound(1, itemLimit, 1000) : 128;

    QJsonArray result;
    for (int ordinal = 0; ordinal < tabWidgets.size(); ++ordinal) {
        QTabWidget* tabWidget = tabWidgets.at(ordinal);
        QJsonArray items;
        const int end = qMin(tabWidget->count(), itemLimit);
        for (int index = 0; index < end; ++index) {
            items.append(QJsonObject{
                {"index", index},
                {"text", tabWidget->tabText(index)},
                {"tooltip", tabWidget->tabToolTip(index)},
                {"whatsThis", tabWidget->tabWhatsThis(index)},
                {"enabled", tabWidget->isTabEnabled(index)},
                {"visible", tabWidget->isTabVisible(index)},
                {"selected", index == tabWidget->currentIndex()},
                {"pageClass", tabWidget->widget(index) == nullptr
                    ? QString()
                    : QString::fromLatin1(
                        tabWidget->widget(index)->metaObject()->className())},
                {"pageObjectName", tabWidget->widget(index) == nullptr
                    ? QString()
                    : tabWidget->widget(index)->objectName()},
            });
        }
        result.append(QJsonObject{
            {"ordinal", ordinal},
            {"class", tabWidget->metaObject()->className()},
            {"objectName", tabWidget->objectName()},
            {"visible", tabWidget->isVisible()},
            {"enabled", tabWidget->isEnabled()},
            {"currentIndex", tabWidget->currentIndex()},
            {"count", tabWidget->count()},
            {"ancestry", ObjectAncestry(tabWidget)},
            {"itemLimit", itemLimit},
            {"itemReturned", items.size()},
            {"hasMoreItems", end < tabWidget->count()},
            {"items", items},
        });
    }
    return result;
}

bool HasTabProbe()
{
    return qEnvironmentVariableIntValue("NSIGHT_SOLID_PROBE_TAB_EXPORT") == 1
        || !EnvironmentFilters("NSIGHT_SOLID_PROBE_TAB_CLASS_MATCH").isEmpty()
        || !EnvironmentFilters("NSIGHT_SOLID_PROBE_TAB_OBJECT_MATCH").isEmpty()
        || !EnvironmentFilters("NSIGHT_SOLID_PROBE_TAB_ANCESTRY_CLASS_MATCH").isEmpty()
        || !EnvironmentFilters("NSIGHT_SOLID_PROBE_TAB_ANCESTRY_OBJECT_MATCH").isEmpty()
        || !qEnvironmentVariable("NSIGHT_SOLID_PROBE_TAB_SELECT_MATCH").trimmed().isEmpty();
}

QString MethodTypeName(QMetaMethod::MethodType type)
{
    switch (type) {
    case QMetaMethod::Signal: return "signal";
    case QMetaMethod::Slot: return "slot";
    case QMetaMethod::Constructor: return "constructor";
    case QMetaMethod::Method: return "method";
    }
    return "unknown";
}

QString MethodAccessName(QMetaMethod::Access access)
{
    switch (access) {
    case QMetaMethod::Private: return "private";
    case QMetaMethod::Protected: return "protected";
    case QMetaMethod::Public: return "public";
    }
    return "unknown";
}

QList<QObject*> DiscoverObjects(QApplication* application)
{
    QSet<QObject*> unique;
    unique.insert(application);
    const auto addObjects = [&unique](const QList<QObject*>& objects) {
        for (QObject* object : objects) {
            if (object != nullptr) {
                unique.insert(object);
            }
        }
    };
    addObjects(application->findChildren<QObject*>());
    const auto topLevels = application->topLevelWidgets();
    for (QWidget* topLevel : topLevels) {
        unique.insert(topLevel);
        addObjects(topLevel->findChildren<QObject*>());
    }
    for (QWidget* widget : application->allWidgets()) {
        unique.insert(widget);
    }

    const QStringList classFilters = EnvironmentFilters(
        "NSIGHT_SOLID_PROBE_OBJECT_CLASS_MATCH");
    const QStringList objectFilters = EnvironmentFilters(
        "NSIGHT_SOLID_PROBE_OBJECT_OBJECT_MATCH");
    const QStringList ancestryClassFilters = EnvironmentFilters(
        "NSIGHT_SOLID_PROBE_OBJECT_ANCESTRY_CLASS_MATCH");
    const QStringList ancestryObjectFilters = EnvironmentFilters(
        "NSIGHT_SOLID_PROBE_OBJECT_ANCESTRY_OBJECT_MATCH");
    const bool exact = qEnvironmentVariable("NSIGHT_SOLID_PROBE_OBJECT_MATCH_MODE")
        .compare("exact", Qt::CaseInsensitive) == 0;

    QList<QObject*> result;
    for (QObject* object : unique) {
        const QString className = QString::fromLatin1(
            object->metaObject()->className());
        if (!TextFilterMatches(className, classFilters, exact)
            || !TextFilterMatches(object->objectName(), objectFilters, exact)
            || !ObjectChainMatches(
                object,
                ancestryClassFilters,
                ancestryObjectFilters,
                exact)) {
            continue;
        }
        result.append(object);
    }
    std::sort(result.begin(), result.end(), [](const auto* left, const auto* right) {
        const QString leftKey = QString::fromLatin1(left->metaObject()->className())
            + "\n" + left->objectName();
        const QString rightKey = QString::fromLatin1(right->metaObject()->className())
            + "\n" + right->objectName();
        if (leftKey != rightKey) {
            return leftKey < rightKey;
        }
        return left < right;
    });
    return result;
}

QList<QObject*> DiscoverInvokeTargets(QApplication* application)
{
    const QStringList classFilters = EnvironmentFilters(
        "NSIGHT_SOLID_PROBE_INVOKE_CLASS_MATCH");
    const QStringList objectFilters = EnvironmentFilters(
        "NSIGHT_SOLID_PROBE_INVOKE_OBJECT_MATCH");
    const QStringList textFilters = EnvironmentFilters(
        "NSIGHT_SOLID_PROBE_INVOKE_TEXT_MATCH");
    const QStringList ancestryClassFilters = EnvironmentFilters(
        "NSIGHT_SOLID_PROBE_INVOKE_ANCESTRY_CLASS_MATCH");
    const QStringList ancestryObjectFilters = EnvironmentFilters(
        "NSIGHT_SOLID_PROBE_INVOKE_ANCESTRY_OBJECT_MATCH");
    const bool exact = qEnvironmentVariable("NSIGHT_SOLID_PROBE_INVOKE_MATCH_MODE")
        .compare("exact", Qt::CaseInsensitive) == 0;

    QSet<QObject*> unique;
    unique.insert(application);
    const auto addObjects = [&unique](const QList<QObject*>& objects) {
        for (QObject* object : objects) {
            if (object != nullptr) {
                unique.insert(object);
            }
        }
    };
    addObjects(application->findChildren<QObject*>());
    for (QWidget* topLevel : application->topLevelWidgets()) {
        unique.insert(topLevel);
        addObjects(topLevel->findChildren<QObject*>());
    }
    for (QWidget* widget : application->allWidgets()) {
        unique.insert(widget);
    }

    QList<QObject*> result;
    for (QObject* object : unique) {
        const QString className = QString::fromLatin1(
            object->metaObject()->className());
        QString text;
        if (auto* button = qobject_cast<QAbstractButton*>(object)) {
            text = button->text();
        } else if (auto* action = qobject_cast<QAction*>(object)) {
            text = action->text();
        } else {
            text = object->property("text").toString();
        }
        if (!TextFilterMatches(className, classFilters, exact)
            || !TextFilterMatches(object->objectName(), objectFilters, exact)
            || !TextFilterMatches(text, textFilters, exact)
            || !ObjectChainMatches(
                object,
                ancestryClassFilters,
                ancestryObjectFilters,
                exact)) {
            continue;
        }
        result.append(object);
    }
    std::sort(result.begin(), result.end(), [](const auto* left, const auto* right) {
        const QString leftKey = QString::fromLatin1(left->metaObject()->className())
            + "\n" + left->objectName();
        const QString rightKey = QString::fromLatin1(right->metaObject()->className())
            + "\n" + right->objectName();
        if (leftKey != rightKey) {
            return leftKey < rightKey;
        }
        return left < right;
    });
    return result;
}

QJsonObject ObjectSummary(QObject* object)
{
    if (object == nullptr) {
        return {};
    }
    QJsonObject summary{
        {"class", object->metaObject()->className()},
        {"objectName", object->objectName()},
        {"id", QString::number(reinterpret_cast<quintptr>(object), 16)},
        {"parentClass", object->parent() == nullptr
            ? QString()
            : QString::fromLatin1(object->parent()->metaObject()->className())},
        {"parentObjectName", object->parent() == nullptr
            ? QString()
            : object->parent()->objectName()},
        {"ancestry", ObjectAncestry(object)},
    };
    if (auto* button = qobject_cast<QAbstractButton*>(object)) {
        summary.insert("text", button->text());
        summary.insert("toolTip", button->toolTip());
        summary.insert("enabled", button->isEnabled());
        summary.insert("visible", button->isVisible());
    } else if (auto* action = qobject_cast<QAction*>(object)) {
        summary.insert("text", action->text());
        summary.insert("toolTip", action->toolTip());
        summary.insert("enabled", action->isEnabled());
        summary.insert("visible", action->isVisible());
    }
    return summary;
}

QJsonArray CollectObjects(QApplication* application, int* totalCount)
{
    const QList<QObject*> objects = DiscoverObjects(application);
    *totalCount = objects.size();
    const bool includeInherited = qEnvironmentVariableIntValue(
        "NSIGHT_SOLID_PROBE_OBJECT_INCLUDE_INHERITED") == 1;
    const bool readProperties = qEnvironmentVariableIntValue(
        "NSIGHT_SOLID_PROBE_OBJECT_READ_PROPERTIES") == 1;
    const QStringList propertyFilters = EnvironmentFilters(
        "NSIGHT_SOLID_PROBE_OBJECT_PROPERTY_MATCH");
    const bool propertyExact = qEnvironmentVariable(
        "NSIGHT_SOLID_PROBE_OBJECT_PROPERTY_MATCH_MODE")
        .compare("exact", Qt::CaseInsensitive) == 0;
    const QStringList methodFilters = EnvironmentFilters(
        "NSIGHT_SOLID_PROBE_OBJECT_METHOD_MATCH");
    const bool methodExact = qEnvironmentVariable(
        "NSIGHT_SOLID_PROBE_OBJECT_METHOD_MATCH_MODE")
        .compare("exact", Qt::CaseInsensitive) == 0;
    const bool includeMethods = !qEnvironmentVariableIsSet(
        "NSIGHT_SOLID_PROBE_OBJECT_INCLUDE_METHODS")
        || qEnvironmentVariableIntValue(
            "NSIGHT_SOLID_PROBE_OBJECT_INCLUDE_METHODS") == 1;

    QJsonArray result;
    for (int ordinal = 0; ordinal < objects.size(); ++ordinal) {
        QObject* object = objects.at(ordinal);
        const QMetaObject* metaObject = object->metaObject();
        QJsonArray properties;
        const int propertyStart = includeInherited ? 0 : metaObject->propertyOffset();
        for (int index = propertyStart; index < metaObject->propertyCount(); ++index) {
            const QMetaProperty property = metaObject->property(index);
            if (!TextFilterMatches(
                    QString::fromLatin1(property.name()),
                    propertyFilters,
                    propertyExact)) {
                continue;
            }
            QJsonObject entry{
                {"index", index},
                {"name", property.name()},
                {"type", property.typeName()},
                {"readable", property.isReadable()},
                {"writable", property.isWritable()},
                {"resettable", property.isResettable()},
                {"constant", property.isConstant()},
                {"final", property.isFinal()},
                {"notifySignal", property.hasNotifySignal()
                    ? QString::fromLatin1(property.notifySignal().methodSignature())
                    : QString()},
            };
            if (readProperties && property.isReadable()) {
                InsertVariantValue(entry, "value", property.read(object), "normal");
            }
            properties.append(entry);
        }

        QJsonArray methods;
        const int methodStart = includeInherited ? 0 : metaObject->methodOffset();
        for (int index = methodStart;
             includeMethods && index < metaObject->methodCount();
             ++index) {
            const QMetaMethod method = metaObject->method(index);
            const QString methodName = QString::fromLatin1(method.name());
            const QString methodSignature = QString::fromLatin1(
                method.methodSignature());
            if (!TextFilterMatches(methodName, methodFilters, methodExact)
                && !TextFilterMatches(
                    methodSignature,
                    methodFilters,
                    methodExact)) {
                continue;
            }
            QJsonArray parameterTypes;
            for (const QByteArray& parameterType : method.parameterTypes()) {
                parameterTypes.append(QString::fromLatin1(parameterType));
            }
            methods.append(QJsonObject{
                {"index", index},
                {"signature", methodSignature},
                {"name", methodName},
                {"type", MethodTypeName(method.methodType())},
                {"access", MethodAccessName(method.access())},
                {"returnType", QString::fromLatin1(method.typeName())},
                {"parameterTypes", parameterTypes},
            });
        }

        QJsonArray dynamicProperties;
        for (const QByteArray& propertyName : object->dynamicPropertyNames()) {
            if (!TextFilterMatches(
                    QString::fromLatin1(propertyName),
                    propertyFilters,
                    propertyExact)) {
                continue;
            }
            QJsonObject property{{"name", QString::fromLatin1(propertyName)}};
            if (readProperties) {
                InsertVariantValue(
                    property,
                    "value",
                    object->property(propertyName.constData()),
                    "normal");
            }
            dynamicProperties.append(property);
        }

        QJsonObject entry{
            {"ordinal", ordinal},
            {"class", metaObject->className()},
            {"objectName", object->objectName()},
            {"id", QString::number(reinterpret_cast<quintptr>(object), 16)},
            {"parentClass", object->parent() == nullptr
                ? QString()
                : QString::fromLatin1(object->parent()->metaObject()->className())},
            {"parentObjectName", object->parent() == nullptr
                ? QString()
                : object->parent()->objectName()},
            {"parentId", object->parent() == nullptr
                ? QString()
                : QString::number(reinterpret_cast<quintptr>(object->parent()), 16)},
            {"ancestry", ObjectAncestry(object)},
            {"properties", properties},
            {"methods", methods},
            {"dynamicProperties", dynamicProperties},
        };
        if (auto* widget = qobject_cast<QWidget*>(object)) {
            entry.insert("widget", true);
            entry.insert("visible", widget->isVisible());
            entry.insert("enabled", widget->isEnabled());
        } else {
            entry.insert("widget", false);
        }
        result.append(entry);
    }
    return result;
}

bool HasObjectProbe()
{
    return !EnvironmentFilters("NSIGHT_SOLID_PROBE_OBJECT_CLASS_MATCH").isEmpty()
        || !EnvironmentFilters("NSIGHT_SOLID_PROBE_OBJECT_OBJECT_MATCH").isEmpty()
        || !EnvironmentFilters(
            "NSIGHT_SOLID_PROBE_OBJECT_ANCESTRY_CLASS_MATCH").isEmpty()
        || !EnvironmentFilters(
            "NSIGHT_SOLID_PROBE_OBJECT_ANCESTRY_OBJECT_MATCH").isEmpty();
}

QJsonArray CollectModels(
    QApplication* application,
    bool includeData,
    int* totalCount,
    bool* missingRequiredFilter)
{
    const QList<QAbstractItemModel*> models = DiscoverModels(application);
    const QHash<QAbstractItemModel*, QJsonArray> attachments =
        BuildModelViewAttachments(application);
    const QStringList classFilters = EnvironmentFilters(
        "NSIGHT_SOLID_PROBE_MODEL_CLASS_MATCH");
    const QStringList objectFilters = EnvironmentFilters(
        "NSIGHT_SOLID_PROBE_MODEL_OBJECT_MATCH");
    const bool exact = qEnvironmentVariable("NSIGHT_SOLID_PROBE_MODEL_MATCH_MODE")
        .compare("exact", Qt::CaseInsensitive) == 0;
    const bool restrictInstance = qEnvironmentVariableIsSet(
        "NSIGHT_SOLID_PROBE_MODEL_INSTANCE_ORDINAL");
    const int requestedInstance = qMax(0, qEnvironmentVariableIntValue(
        "NSIGHT_SOLID_PROBE_MODEL_INSTANCE_ORDINAL"));
    const QStringList attachedViewClassFilters = EnvironmentFilters(
        "NSIGHT_SOLID_PROBE_MODEL_ATTACHED_VIEW_CLASS_MATCH");
    const QStringList attachedViewObjectFilters = EnvironmentFilters(
        "NSIGHT_SOLID_PROBE_MODEL_ATTACHED_VIEW_OBJECT_MATCH");
    const bool requireVisibleView = qEnvironmentVariableIntValue(
        "NSIGHT_SOLID_PROBE_MODEL_REQUIRE_VISIBLE_VIEW") == 1;
    const QStringList sharedParentViewClassFilters = EnvironmentFilters(
        "NSIGHT_SOLID_PROBE_MODEL_SHARE_PARENT_WITH_VISIBLE_VIEW_CLASS_MATCH");
    const QStringList sharedParentViewObjectFilters = EnvironmentFilters(
        "NSIGHT_SOLID_PROBE_MODEL_SHARE_PARENT_WITH_VISIBLE_VIEW_OBJECT_MATCH");
    QSet<QObject*> sharedViewParents;
    if (!sharedParentViewClassFilters.isEmpty()
        || !sharedParentViewObjectFilters.isEmpty()) {
        for (QAbstractItemModel* anchorModel : models) {
            for (const QJsonValue& attachmentValue : attachments.value(anchorModel)) {
                const QJsonObject attachment = attachmentValue.toObject();
                if (attachment.value("visible").toBool()
                    && TextFilterMatches(
                        attachment.value("viewClass").toString(),
                        sharedParentViewClassFilters,
                        exact)
                    && TextFilterMatches(
                        attachment.value("viewObjectName").toString(),
                        sharedParentViewObjectFilters,
                        exact)
                    && anchorModel->parent() != nullptr) {
                    sharedViewParents.insert(anchorModel->parent());
                }
            }
        }
    }
    *totalCount = models.size();
    *missingRequiredFilter = includeData && classFilters.isEmpty() && objectFilters.isEmpty();

    QHash<QString, int> instanceOrdinals;
    QJsonArray result;
    if (*missingRequiredFilter) {
        return result;
    }
    for (QAbstractItemModel* model : models) {
        const QString className = QString::fromLatin1(model->metaObject()->className());
        const QString objectName = model->objectName();
        if (!TextFilterMatches(className, classFilters, exact)
            || !TextFilterMatches(objectName, objectFilters, exact)) {
            continue;
        }
        if ((!sharedParentViewClassFilters.isEmpty()
                || !sharedParentViewObjectFilters.isEmpty())
            && !sharedViewParents.contains(model->parent())) {
            continue;
        }

        const QJsonArray modelAttachments = attachments.value(model);
        if (!attachedViewClassFilters.isEmpty()
            || !attachedViewObjectFilters.isEmpty()
            || requireVisibleView) {
            bool attachmentMatched = false;
            for (const QJsonValue& attachmentValue : modelAttachments) {
                const QJsonObject attachment = attachmentValue.toObject();
                if (TextFilterMatches(
                        attachment.value("viewClass").toString(),
                        attachedViewClassFilters,
                        exact)
                    && TextFilterMatches(
                        attachment.value("viewObjectName").toString(),
                        attachedViewObjectFilters,
                        exact)
                    && (!requireVisibleView
                        || attachment.value("visible").toBool())) {
                    attachmentMatched = true;
                    break;
                }
            }
            if (!attachmentMatched) {
                continue;
            }
        }

        const QString identity = className + "\n" + objectName;
        const int instanceOrdinal = instanceOrdinals.value(identity, 0);
        instanceOrdinals.insert(identity, instanceOrdinal + 1);
        if (restrictInstance && instanceOrdinal != requestedInstance) {
            continue;
        }
        QJsonArray dynamicProperties;
        for (const QByteArray& propertyName : model->dynamicPropertyNames()) {
            dynamicProperties.append(QString::fromLatin1(propertyName));
        }

        QJsonObject entry{
            {"class", className},
            {"objectName", objectName},
            {"instanceOrdinal", instanceOrdinal},
            {"parentClass", model->parent() == nullptr
                ? QString()
                : QString::fromLatin1(model->parent()->metaObject()->className())},
            {"parentObjectName", model->parent() == nullptr
                ? QString()
                : model->parent()->objectName()},
            {"parentId", model->parent() == nullptr
                ? QString()
                : QString::number(
                    reinterpret_cast<quintptr>(model->parent()), 16)},
            {"ancestry", ObjectAncestry(model)},
            {"dynamicPropertyNames", dynamicProperties},
            {"attachedViews", modelAttachments},
        };
        if (includeData) {
            if (qEnvironmentVariableIntValue(
                    "NSIGHT_SOLID_PROBE_MODEL_STRUCTURE_ONLY") == 1) {
                entry.insert("export", QJsonObject{
                    {"modelClass", model->metaObject()->className()},
                    {"modelObjectName", model->objectName()},
                    {"rootRows", model->rowCount()},
                    {"rootColumns", model->columnCount()},
                    {"structureOnly", true},
                });
            } else {
                ExportOptions options = ReadExportOptions("MODEL");
                const bool explicitColumns = !options.columns.isEmpty();
                const bool forceAllColumns = qEnvironmentVariable(
                    "NSIGHT_SOLID_PROBE_MODEL_COLUMN_POLICY")
                    .compare("all", Qt::CaseInsensitive) == 0;
                if (!explicitColumns && !forceAllColumns) {
                    QSet<int> visibleColumns;
                    bool hasViewProjection = false;
                    const int sourceColumns = model->columnCount();
                    for (const QJsonValue& attachmentValue : modelAttachments) {
                        const QJsonObject attachment = attachmentValue.toObject();
                        const QJsonArray attachmentColumns = attachment.value(
                            "visibleColumns").toArray();
                        if (!attachmentColumns.isEmpty()
                            && (!attachment.value("hiddenColumns").toArray().isEmpty()
                                || attachment.value("viewRootColumns").toInt()
                                    < sourceColumns)) {
                            hasViewProjection = true;
                            for (const QJsonValue& column : attachmentColumns) {
                                visibleColumns.insert(column.toInt());
                            }
                        }
                    }
                    if (hasViewProjection && !visibleColumns.isEmpty()) {
                        options.columns = visibleColumns.values();
                        std::sort(options.columns.begin(), options.columns.end());
                        entry.insert("columnPolicy", "view-visible");
                    }
                }
                if (!entry.contains("columnPolicy")) {
                    entry.insert("columnPolicy", explicitColumns ? "explicit" : "all");
                }
                if (!options.flat
                    && className.contains("InstructionMixModel", Qt::CaseInsensitive)) {
                    options.flat = true;
                    entry.insert("flatPolicy", "instruction-mix-default");
                } else {
                    entry.insert("flatPolicy", options.flat ? "explicit" : "tree");
                }
                entry.insert("export", ExportModel(model, options));
            }
        }
        result.append(entry);
    }
    return result;
}

QString NormalizeEventRange(QString value)
{
    value.remove(' ');
    return value;
}

struct EventSearchState
{
    QString value;
    int column = 0;
    int occurrence = 0;
    int visited = 0;
    int matched = 0;
    int maxVisited = 1000000;
    int targetOrdinal = -1;
    bool exact = true;
    bool normalizeRange = false;
};

QModelIndex SearchEventIndex(
    QAbstractItemModel* model,
    const QModelIndex& parent,
    int depth,
    EventSearchState& state)
{
    if (model == nullptr || depth >= 64 || state.visited >= state.maxVisited) {
        return {};
    }

    const int rows = model->rowCount(parent);
    for (int row = 0; row < rows; ++row) {
        if (state.visited >= state.maxVisited) {
            return {};
        }

        const QModelIndex index = model->index(row, 0, parent);
        if (!index.isValid()) {
            continue;
        }

        const int ordinal = state.visited++;
        bool matches = state.targetOrdinal >= 0 && ordinal == state.targetOrdinal;
        if (state.targetOrdinal < 0) {
            QString candidate = model->data(
                model->index(row, state.column, parent), Qt::DisplayRole).toString();
            if (state.normalizeRange) {
                candidate = NormalizeEventRange(candidate);
            }
            matches = state.exact
                ? candidate.compare(state.value, Qt::CaseInsensitive) == 0
                : candidate.contains(state.value, Qt::CaseInsensitive);
        }
        if (matches && state.matched++ == state.occurrence) {
            return index;
        }

        const QModelIndex childMatch = SearchEventIndex(model, index, depth + 1, state);
        if (childMatch.isValid()) {
            return childMatch;
        }
    }
    return {};
}

QModelIndex FindIndexByPath(QAbstractItemModel* model, const QString& path, bool* validPath)
{
    *validPath = false;
    if (model == nullptr || path.trimmed().isEmpty()) {
        return {};
    }

    QModelIndex parent;
    const QStringList parts = path.split('/', Qt::SkipEmptyParts);
    if (parts.isEmpty() || parts.size() > 64) {
        return {};
    }
    for (const QString& part : parts) {
        bool ok = false;
        const int row = part.toInt(&ok);
        if (!ok || row < 0 || row >= model->rowCount(parent)) {
            return {};
        }
        parent = model->index(row, 0, parent);
        if (!parent.isValid()) {
            return {};
        }
    }
    *validPath = true;
    return parent;
}

QModelIndex ResolveEventIndex(
    QAbstractItemModel* model,
    QJsonObject* selector,
    int* visited)
{
    const QString path = qEnvironmentVariable("NSIGHT_SOLID_PROBE_EVENT_PATH").trimmed();
    const QString range = qEnvironmentVariable("NSIGHT_SOLID_PROBE_EVENT_RANGE").trimmed();
    const QString description = qEnvironmentVariable(
        "NSIGHT_SOLID_PROBE_EVENT_MATCH", "DescriptorHeapSample::onRender").trimmed();
    const int occurrence = qMax(
        0, qEnvironmentVariableIntValue("NSIGHT_SOLID_PROBE_EVENT_OCCURRENCE"));

    if (!path.isEmpty()) {
        bool validPath = false;
        const QModelIndex index = FindIndexByPath(model, path, &validPath);
        selector->insert("kind", "path");
        selector->insert("value", path);
        selector->insert("validPath", validPath);
        *visited = 0;
        return index;
    }

    EventSearchState state;
    state.occurrence = occurrence;
    const int configuredSearchLimit = qEnvironmentVariableIntValue(
        "NSIGHT_SOLID_PROBE_EVENT_SEARCH_LIMIT");
    if (configuredSearchLimit > 0) {
        state.maxVisited = qBound(1, configuredSearchLimit, 5000000);
    }

    const QByteArray ordinalKey("NSIGHT_SOLID_PROBE_EVENT_ORDINAL");
    if (!range.isEmpty()) {
        state.value = NormalizeEventRange(range);
        state.column = 1;
        state.normalizeRange = true;
        selector->insert("kind", "range");
        selector->insert("value", range);
    } else if (qEnvironmentVariableIsSet(ordinalKey.constData())) {
        state.targetOrdinal = qMax(
            0, qEnvironmentVariableIntValue(ordinalKey.constData()));
        state.occurrence = 0;
        selector->insert("kind", "ordinal");
        selector->insert("value", state.targetOrdinal);
    } else {
        state.value = description;
        state.column = 0;
        state.exact = qEnvironmentVariable("NSIGHT_SOLID_PROBE_EVENT_MATCH_MODE")
            .compare("contains", Qt::CaseInsensitive) != 0;
        selector->insert("kind", "description");
        selector->insert("value", description);
        selector->insert("matchMode", state.exact ? "exact" : "contains");
    }
    selector->insert("occurrence", state.occurrence);

    const QModelIndex index = SearchEventIndex(model, {}, 0, state);
    selector->insert("matchesVisited", state.matched);
    selector->insert("searchLimit", state.maxVisited);
    *visited = state.visited;
    return index;
}

QJsonObject IndexSummary(QAbstractItemModel* model, const QModelIndex& index)
{
    if (model == nullptr || !index.isValid()) {
        return QJsonObject{{"valid", false}};
    }

    QJsonArray path;
    QModelIndex cursor = index;
    while (cursor.isValid()) {
        path.prepend(cursor.row());
        cursor = cursor.parent();
    }

    QJsonArray cells;
    const QModelIndex parent = index.parent();
    const int columns = model->columnCount(parent);
    for (int column = 0; column < columns; ++column) {
        cells.append(VariantToJson(
            model->data(model->index(index.row(), column, parent), Qt::DisplayRole)));
    }
    return QJsonObject{
        {"valid", true},
        {"path", path},
        {"row", index.row()},
        {"column", index.column()},
        {"cells", cells},
    };
}

QAbstractItemView* FindEventView(QApplication* application)
{
    const auto widgets = application->allWidgets();
    for (QWidget* widget : widgets) {
        if (widget != nullptr && widget->objectName() == "EventList_EventTreeView") {
            if (auto* view = qobject_cast<QAbstractItemView*>(widget)) {
                return view;
            }
        }
    }
    return nullptr;
}

bool HasModelRowSelector()
{
    return qEnvironmentVariableIsSet("NSIGHT_SOLID_PROBE_MODEL_SELECT_PATH")
        || qEnvironmentVariableIsSet("NSIGHT_SOLID_PROBE_MODEL_SELECT_ROW")
        || !qEnvironmentVariable("NSIGHT_SOLID_PROBE_MODEL_SELECT_MATCH").trimmed().isEmpty();
}

QAbstractItemView* FindModelSelectionView(QApplication* application)
{
    const QString objectName = qEnvironmentVariable(
        "NSIGHT_SOLID_PROBE_MODEL_SELECT_VIEW_OBJECT", "SampleTreeView").trimmed();
    const auto widgets = application->allWidgets();
    for (QWidget* widget : widgets) {
        if (widget == nullptr || widget->objectName() != objectName) {
            continue;
        }
        if (auto* view = qobject_cast<QAbstractItemView*>(widget)) {
            return view;
        }
    }
    return nullptr;
}

QWidget* FindNamedWidget(QApplication* application, const QString& objectName)
{
    const auto widgets = application->allWidgets();
    for (QWidget* widget : widgets) {
        if (widget != nullptr && widget->objectName() == objectName) {
            return widget;
        }
    }
    return nullptr;
}

QModelIndex ResolveModelSelectionIndex(
    QAbstractItemModel* model,
    QJsonObject* selector,
    int* visited)
{
    const QString path = qEnvironmentVariable(
        "NSIGHT_SOLID_PROBE_MODEL_SELECT_PATH").trimmed();
    const QByteArray rowKey("NSIGHT_SOLID_PROBE_MODEL_SELECT_ROW");
    const QString match = qEnvironmentVariable(
        "NSIGHT_SOLID_PROBE_MODEL_SELECT_MATCH").trimmed();

    selector->insert("viewObjectName", qEnvironmentVariable(
        "NSIGHT_SOLID_PROBE_MODEL_SELECT_VIEW_OBJECT", "SampleTreeView").trimmed());
    if (!path.isEmpty()) {
        bool validPath = false;
        const QModelIndex index = FindIndexByPath(model, path, &validPath);
        selector->insert("kind", "path");
        selector->insert("value", path);
        selector->insert("validPath", validPath);
        *visited = 0;
        return index;
    }

    if (qEnvironmentVariableIsSet(rowKey.constData())) {
        const int row = qEnvironmentVariableIntValue(rowKey.constData());
        const bool validRow = row >= 0 && model != nullptr && row < model->rowCount();
        selector->insert("kind", "row");
        selector->insert("value", row);
        selector->insert("validRow", validRow);
        *visited = 0;
        return validRow ? model->index(row, 0) : QModelIndex();
    }

    EventSearchState state;
    state.value = match;
    state.column = qMax(0, qEnvironmentVariableIntValue(
        "NSIGHT_SOLID_PROBE_MODEL_SELECT_COLUMN"));
    state.occurrence = qMax(0, qEnvironmentVariableIntValue(
        "NSIGHT_SOLID_PROBE_MODEL_SELECT_OCCURRENCE"));
    state.exact = qEnvironmentVariable("NSIGHT_SOLID_PROBE_MODEL_SELECT_MATCH_MODE")
        .compare("contains", Qt::CaseInsensitive) != 0;
    const int searchLimit = qEnvironmentVariableIntValue(
        "NSIGHT_SOLID_PROBE_MODEL_SELECT_SEARCH_LIMIT");
    if (searchLimit > 0) {
        state.maxVisited = qBound(1, searchLimit, 1000000);
    }

    selector->insert("kind", "match");
    selector->insert("value", match);
    selector->insert("column", state.column);
    selector->insert("occurrence", state.occurrence);
    selector->insert("matchMode", state.exact ? "exact" : "contains");
    const QModelIndex index = SearchEventIndex(model, {}, 0, state);
    selector->insert("matchesVisited", state.matched);
    selector->insert("searchLimit", state.maxVisited);
    *visited = state.visited;
    return index;
}

bool SameIndexPath(const QJsonObject& left, const QJsonObject& right)
{
    if (!left.value("valid").toBool() || !right.value("valid").toBool()) {
        return false;
    }
    return QJsonDocument(left.value("path").toArray()).toJson(QJsonDocument::Compact)
        == QJsonDocument(right.value("path").toArray()).toJson(QJsonDocument::Compact);
}

bool SameIndexCellValue(
    const QJsonObject& left,
    const QJsonObject& right,
    int column)
{
    if (!left.value("valid").toBool() || !right.value("valid").toBool()) {
        return false;
    }
    const QJsonArray leftCells = left.value("cells").toArray();
    const QJsonArray rightCells = right.value("cells").toArray();
    if (column < 0 || column >= leftCells.size() || column >= rightCells.size()) {
        return SameIndexPath(left, right);
    }
    return QJsonDocument(QJsonArray{leftCells.at(column)})
        .toJson(QJsonDocument::Compact)
        == QJsonDocument(QJsonArray{rightCells.at(column)})
            .toJson(QJsonDocument::Compact);
}

int CountChangedMetricViews(const QJsonArray& before, const QJsonArray& after)
{
    QHash<QString, QByteArray> beforeByIdentity;
    for (const QJsonValue& value : before) {
        const QJsonObject view = value.toObject();
        const QString identity = view.value("viewObjectName").toString()
            + "#" + QString::number(view.value("instanceOrdinal").toInt());
        beforeByIdentity.insert(identity, QJsonDocument(view.value("export").toObject())
            .toJson(QJsonDocument::Compact));
    }

    int changed = 0;
    for (const QJsonValue& value : after) {
        const QJsonObject view = value.toObject();
        const QString identity = view.value("viewObjectName").toString()
            + "#" + QString::number(view.value("instanceOrdinal").toInt());
        const QByteArray current = QJsonDocument(view.value("export").toObject())
            .toJson(QJsonDocument::Compact);
        if (!beforeByIdentity.contains(identity) || beforeByIdentity.value(identity) != current) {
            ++changed;
        }
    }
    return changed;
}

bool HasModelExportFilters()
{
    return !EnvironmentFilters("NSIGHT_SOLID_PROBE_MODEL_CLASS_MATCH").isEmpty()
        || !EnvironmentFilters("NSIGHT_SOLID_PROBE_MODEL_OBJECT_MATCH").isEmpty();
}

QByteArray ModelsSnapshot(const QJsonArray& models)
{
    return QJsonDocument(models).toJson(QJsonDocument::Compact);
}

bool HasPopulatedModel(const QJsonArray& models)
{
    for (const QJsonValue& value : models) {
        const QJsonObject exportObject = value.toObject().value("export").toObject();
        if (exportObject.value("rootRows").toInt() > 0
            || exportObject.value("totalCount").toInt() > 0) {
            return true;
        }
    }
    return false;
}

bool AddJsonNumbers(const QJsonValue& value, qint64* total)
{
    if (value.isDouble()) {
        *total += static_cast<qint64>(value.toDouble());
        return true;
    }
    if (value.isString()) {
        bool ok = false;
        const qint64 number = value.toString().toLongLong(&ok);
        if (ok) {
            *total += number;
        }
        return ok;
    }
    if (value.isArray()) {
        bool found = false;
        for (const QJsonValue& child : value.toArray()) {
            found = AddJsonNumbers(child, total) || found;
        }
        return found;
    }
    return false;
}

bool IndexSummaryColumnSum(
    const QJsonObject& summary,
    int column,
    qint64* total)
{
    const QJsonArray cells = summary.value("cells").toArray();
    if (column < 0 || column >= cells.size()) {
        return false;
    }
    *total = 0;
    return AddJsonNumbers(cells.at(column), total);
}

bool ModelsColumnSum(const QJsonArray& models, int column, qint64* total)
{
    *total = 0;
    bool found = false;
    for (const QJsonValue& modelValue : models) {
        const QJsonArray nodes = modelValue.toObject()
            .value("export").toObject().value("nodes").toArray();
        for (const QJsonValue& nodeValue : nodes) {
            const QJsonArray cells = nodeValue.toObject().value("cells").toArray();
            for (const QJsonValue& cellValue : cells) {
                const QJsonObject cell = cellValue.toObject();
                if (cell.value("column").toInt(-1) != column) {
                    continue;
                }
                found = AddJsonNumbers(cell.value("display"), total) || found;
            }
        }
    }
    return found;
}

bool IsKnownMode(const QString& mode)
{
    return mode == "heartbeat"
        || mode == "event-discovery"
        || mode == "event-export"
        || mode == "metrics-discovery"
        || mode == "metrics-export"
        || mode == "model-catalog"
        || mode == "model-export"
        || mode == "selection-metrics-export";
}

int CountChangedModels(const QJsonArray& before, const QJsonArray& after)
{
    QHash<QString, QByteArray> beforeByIdentity;
    for (const QJsonValue& value : before) {
        const QJsonObject model = value.toObject();
        const QString identity = model.value("class").toString()
            + "\n" + model.value("objectName").toString()
            + "#" + QString::number(model.value("instanceOrdinal").toInt());
        beforeByIdentity.insert(identity, QJsonDocument(model.value("export").toObject())
            .toJson(QJsonDocument::Compact));
    }

    int changed = 0;
    for (const QJsonValue& value : after) {
        const QJsonObject model = value.toObject();
        const QString identity = model.value("class").toString()
            + "\n" + model.value("objectName").toString()
            + "#" + QString::number(model.value("instanceOrdinal").toInt());
        const QByteArray current = QJsonDocument(model.value("export").toObject())
            .toJson(QJsonDocument::Compact);
        if (!beforeByIdentity.contains(identity) || beforeByIdentity.value(identity) != current) {
            ++changed;
        }
    }
    return changed;
}

const QSet<QString>& ProductSessionSettingNames()
{
    static const QSet<QString> names{
        "ACTION_MATCH_MODE",
        "ACTION_SETTLE_MIN_POLLS",
        "ACTION_TRIGGER_ASYNC",
        "ACTION_TRIGGER_OCCURRENCE",
        "ACTION_TRIGGER_TEXT_MATCH",
        "ACTIVATE_PANEL",
        "ACTIVATE_PANEL_VIA_BUTTON",
        "CLOSE_MODAL_BEFORE_QUIT",
        "COMBO_ANCESTRY_CLASS_MATCH",
        "COMBO_MATCH_MODE",
        "COMBO_OBJECT_MATCH",
        "COMBO_SELECT_FROM_MODEL_SELECTION",
        "COMBO_SELECT_MATCH",
        "COMBO_SELECT_MATCH_MODE",
        "COMBO_SELECT_OCCURRENCE",
        "COMBO_SELECT_SETTLE_MIN_POLLS",
        "COMBO_SELECT_TRIGGER",
        "DIALOG_AUTO_PATH",
        "EVENT_INCLUDE_ITEM_DATA",
        "EVENT_LIMIT",
        "EVENT_OFFSET",
        "EVENT_ORDINAL",
        "EVENT_PATH",
        "EXTERNAL_KILL_ON_TIMEOUT",
        "INVOKE_CLASS_MATCH",
        "INVOKE_MATCH_MODE",
        "INVOKE_METHOD",
        "INVOKE_OBJECT_MATCH",
        "INVOKE_OCCURRENCE",
        "INVOKE_SETTLE_MIN_POLLS",
        "MAX_DEPTH",
        "METRIC_LIMIT",
        "METRIC_SETTLE_MAX_POLLS",
        "METRIC_SETTLE_MIN_POLLS",
        "MODEL_ATTACHED_VIEW_OBJECT_MATCH",
        "MODEL_CLASS_MATCH",
        "MODEL_COLUMNS",
        "MODEL_FLAT",
        "MODEL_LIMIT",
        "MODEL_MATCH_MODE",
        "MODEL_OBJECT_MATCH",
        "MODEL_REQUIRED_MIN_COUNT",
        "MODEL_SELECT_COLUMN",
        "MODEL_SELECT_DEFER_PROVIDER_VERIFY_UNTIL_COMBO",
        "MODEL_SELECT_MATCH",
        "MODEL_SELECT_MATCH_MODE",
        "MODEL_SELECT_OCCURRENCE",
        "MODEL_SELECT_PREPARE_PANEL",
        "MODEL_SELECT_PREPARE_PANEL_SETTLE_MIN_POLLS",
        "MODEL_SELECT_PROVIDER_STABLE_MIN_POLLS",
        "MODEL_SELECT_SETTLE_MIN_POLLS",
        "MODEL_SELECT_VIEW_OBJECT",
        "MODEL_SETTLE_MIN_POLLS",
        "PANEL_SETTLE_MIN_POLLS",
        "SELECTION_BASELINE_METRIC_MIN_COUNT",
        "SELECTION_BASELINE_METRIC_STABLE_MIN_POLLS",
    };
    return names;
}

bool IsProductSessionMode(const QString& mode)
{
    return mode == "heartbeat"
        || mode == "event-export"
        || mode == "selection-metrics-export";
}

QString ExpectedProductSchema(const QString& mode)
{
    if (mode == "heartbeat") {
        return "NsightSolidProbeHeartbeatV1";
    }
    if (mode == "event-export") {
        return "NsightSolidProbeEventListV1";
    }
    if (mode == "selection-metrics-export") {
        return "NsightSolidProbeSelectionMetricsV1";
    }
    return {};
}

void ClearProductRequestEnvironment()
{
    for (const QString& name : ProductSessionSettingNames()) {
        qunsetenv(("NSIGHT_SOLID_PROBE_" + name).toUtf8().constData());
    }
    for (const char* name : {
             "NSIGHT_SOLID_PROBE_OUTPUT",
             "NSIGHT_SOLID_PROBE_MODE",
             "NSIGHT_SOLID_PROBE_REQUEST_ID",
             "NSIGHT_SOLID_PROBE_QUIT_AFTER_HEARTBEAT",
             "NSIGHT_SOLID_PROBE_QUIT_WHEN_READY"}) {
        qunsetenv(name);
    }
}

bool IsPathInside(const QString& path, const QString& directory)
{
    const QString normalizedPath = QDir::fromNativeSeparators(
        QDir::cleanPath(QFileInfo(path).absoluteFilePath()));
    QString normalizedDirectory = QDir::fromNativeSeparators(QDir::cleanPath(
        QFileInfo(directory).absoluteFilePath()));
    if (!normalizedDirectory.endsWith('/')) {
        normalizedDirectory.append('/');
    }
#ifdef Q_OS_WIN
    return normalizedPath.startsWith(normalizedDirectory, Qt::CaseInsensitive);
#else
    return normalizedPath.startsWith(normalizedDirectory, Qt::CaseSensitive);
#endif
}

class SolidProbeAgent final : public QObject
{
    Q_OBJECT

public:
    explicit SolidProbeAgent(QObject* parent = nullptr)
        : QObject(parent)
        , m_outputPath(qEnvironmentVariable("NSIGHT_SOLID_PROBE_OUTPUT"))
        , m_mode(qEnvironmentVariable("NSIGHT_SOLID_PROBE_MODE", "heartbeat"))
    {
        if (!qEnvironmentVariable(
                "NSIGHT_SOLID_PROBE_DIALOG_AUTO_PATH").trimmed().isEmpty()) {
            QCoreApplication::setAttribute(Qt::AA_DontUseNativeDialogs, true);
        }
        m_timer.setInterval(500);
        connect(&m_timer, &QTimer::timeout, this, &SolidProbeAgent::Poll);
        QTimer::singleShot(0, this, [this] {
            Poll();
            if (!m_finished) {
                m_timer.start();
            }
        });
    }

signals:
    void Finished(bool reusable);

private slots:
    void Poll()
    {
        ++m_pollCount;

        auto* application = qobject_cast<QApplication*>(QCoreApplication::instance());
        QJsonObject root{
            {"schema", "NsightSolidProbeHeartbeatV1"},
            {"status", m_mode == "heartbeat" && m_pollCount < 10
                ? "loading"
                : "loaded"},
            {"pluginVersion", kPluginVersion},
            {"mode", m_mode},
            {"pid", static_cast<qint64>(QCoreApplication::applicationPid())},
            {"qtCompileVersion", QT_VERSION_STR},
            {"qtRuntimeVersion", qVersion()},
            {"pollCount", m_pollCount},
            {"applicationFound", application != nullptr},
            {"guiThread", application != nullptr && QThread::currentThread() == application->thread()},
            {"verifiedHostTarget", QJsonObject{
                {"nsightVersion", kVerifiedNsightVersion},
                {"nsightBuild", kVerifiedNsightBuild},
            }},
        };
        const QString requestId = qEnvironmentVariable(
            "NSIGHT_SOLID_PROBE_REQUEST_ID").trimmed();
        const QString reportId = qEnvironmentVariable(
            "NSIGHT_SOLID_PROBE_REPORT_ID").trimmed();
        if (!requestId.isEmpty()) {
            root.insert("requestId", requestId);
        }
        if (!reportId.isEmpty()) {
            root.insert("reportId", reportId);
        }

        bool eventListReady = false;
        bool eventExportReady = false;
        bool metricsReady = false;
        bool modelModeReady = false;
        if (application != nullptr) {
            root.insert("applicationClass", application->metaObject()->className());
            root.insert("applicationName", QCoreApplication::applicationName());
            root.insert("applicationVersion", QCoreApplication::applicationVersion());
            root.insert("applicationFilePath", QCoreApplication::applicationFilePath());
            root.insert("topLevelWindowCount", application->topLevelWidgets().size());
            HandleDialogAutomation(application, root);
        }

        if (!IsKnownMode(m_mode)) {
            root.insert("status", "error");
            root.insert("stage", "unsupported-mode");
            root.insert("error", "unsupported probe mode");
            Write(root);
            Finish(false);
            if (qEnvironmentVariableIntValue("NSIGHT_SOLID_PROBE_QUIT_WHEN_READY") == 1) {
                QTimer::singleShot(0, QCoreApplication::instance(), &QCoreApplication::quit);
            }
            return;
        }

        if (application != nullptr && m_mode == "selection-metrics-export") {
            const int configuredPollLimit = qEnvironmentVariableIntValue(
                "NSIGHT_SOLID_PROBE_METRIC_SETTLE_MAX_POLLS");
            const int selectionMetricsPollLimit = configuredPollLimit > 0
                ? qBound(120, configuredPollLimit, 7200)
                : 120;
            root.insert("selectionMetricsPollLimit", selectionMetricsPollLimit);
            const bool complete = HandleSelectionMetrics(application, root);
            if (!complete && m_pollCount >= selectionMetricsPollLimit) {
                root.insert("status", "timeout");
                root.insert("stage", "timeout");
            }
            Write(root);
            if (complete || m_pollCount >= selectionMetricsPollLimit) {
                Finish(complete);
                const bool leaveTimeoutForExternalKill = !complete
                    && m_pollCount >= selectionMetricsPollLimit
                    && qEnvironmentVariableIntValue(
                        "NSIGHT_SOLID_PROBE_EXTERNAL_KILL_ON_TIMEOUT") == 1;
                if (!leaveTimeoutForExternalKill
                    && qEnvironmentVariableIntValue(
                        "NSIGHT_SOLID_PROBE_QUIT_WHEN_READY") == 1) {
                    const bool closeModalBeforeQuit = qEnvironmentVariableIntValue(
                        "NSIGHT_SOLID_PROBE_CLOSE_MODAL_BEFORE_QUIT") == 1;
                    if (closeModalBeforeQuit) {
                        const auto topLevels = application->topLevelWidgets();
                        for (QWidget* topLevel : topLevels) {
                            if (topLevel != nullptr && topLevel->isModal()) {
                                topLevel->close();
                            }
                        }
                    }
                    QTimer::singleShot(
                        closeModalBeforeQuit ? 250 : 0,
                        QCoreApplication::instance(),
                        &QCoreApplication::quit);
                }
            }
            return;
        }

        if (application != nullptr
            && (m_mode == "event-discovery" || m_mode == "event-export")) {
            QJsonArray matches;
            int minimumEventPoll = qEnvironmentVariableIntValue(
                "NSIGHT_SOLID_PROBE_EVENT_MIN_POLL");
            minimumEventPoll = minimumEventPoll > 0 ? minimumEventPoll : 6;
            const auto widgets = application->allWidgets();
            for (QWidget* widget : widgets) {
                if (widget == nullptr || widget->objectName() != "EventList_EventTreeView") {
                    continue;
                }

                QJsonObject match{
                    {"viewClass", widget->metaObject()->className()},
                    {"viewObjectName", widget->objectName()},
                };
                if (auto* view = qobject_cast<QAbstractItemView*>(widget)) {
                    if (auto* model = view->model()) {
                        const int rows = model->rowCount();
                        const int columns = model->columnCount();
                        match.insert("modelClass", model->metaObject()->className());
                        match.insert("modelObjectName", model->objectName());
                        match.insert("rows", rows);
                        match.insert("columns", columns);
                        const bool eventModelPopulated = rows > 0 && columns > 0;
                        eventListReady = eventListReady || (eventModelPopulated
                            && m_pollCount >= minimumEventPoll);
                        match.insert("modelPopulated", eventModelPopulated);
                        match.insert("minimumEventPoll", minimumEventPoll);

                        if (eventListReady && m_mode == "event-export") {
                            int minimumPoll = qEnvironmentVariableIntValue(
                                "NSIGHT_SOLID_PROBE_EVENT_EXPORT_MIN_POLL");
                            minimumPoll = minimumPoll > 0 ? minimumPoll : minimumEventPoll;
                            minimumPoll = qMax(minimumEventPoll, minimumPoll);
                            eventExportReady = m_pollCount >= minimumPoll;
                            match.insert("minimumExportPoll", minimumPoll);
                            match.insert("exportReady", eventExportReady);
                        }

                        if (eventExportReady && m_mode == "event-export") {
                            QAbstractItemModel* extractionModel = model;
                            if (auto* proxy = qobject_cast<QAbstractProxyModel*>(model)) {
                                if (proxy->sourceModel() != nullptr) {
                                    extractionModel = proxy->sourceModel();
                                    match.insert("extractionModel", "source");
                                }
                            }
                            match.insert("export", ExportModel(
                                extractionModel, ReadExportOptions("EVENT")));
                        }
                    }
                }
                matches.append(match);
            }
            root.insert("eventViews", matches);
            root.insert("eventListReady", eventListReady);
            root.insert("schema", m_mode == "event-export"
                ? "NsightSolidProbeEventListV1"
                : "NsightSolidProbeEventDiscoveryV1");
            root.insert("status", (m_mode == "event-export"
                    ? eventExportReady
                    : eventListReady)
                ? "complete"
                : "loading");
        }

        if (application != nullptr
            && (m_mode == "metrics-discovery" || m_mode == "metrics-export")) {
            int metricViewTotal = 0;
            bool metricModelsPopulated = false;
            const QJsonArray matches = CollectMetricViews(
                application,
                m_mode == "metrics-export",
                &metricModelsPopulated,
                &metricViewTotal);
            const QByteArray metricSnapshot = QJsonDocument(QJsonObject{
                {"metricViewTotal", metricViewTotal},
                {"metricViews", matches},
            }).toJson(QJsonDocument::Compact);
            if (metricModelsPopulated
                && !m_lastStandaloneMetricSnapshot.isEmpty()
                && m_lastStandaloneMetricSnapshot == metricSnapshot) {
                ++m_stableStandaloneMetricSamples;
            } else {
                m_stableStandaloneMetricSamples = 0;
            }
            m_lastStandaloneMetricSnapshot = metricSnapshot;
            int minimumMetricPoll = qEnvironmentVariableIntValue(
                "NSIGHT_SOLID_PROBE_METRICS_MIN_POLL");
            minimumMetricPoll = minimumMetricPoll > 0 ? minimumMetricPoll : 10;
            metricsReady = metricModelsPopulated
                && FindEventView(application) != nullptr
                && m_pollCount >= minimumMetricPoll
                && m_stableStandaloneMetricSamples >= 1;
            root.insert(
                "schema",
                m_mode == "metrics-export"
                    ? "NsightSolidProbeMetricsV1"
                    : "NsightSolidProbeMetricsDiscoveryV1");
            root.insert("metricViews", matches);
            root.insert("metricViewTotal", metricViewTotal);
            root.insert("metricViewReturned", matches.size());
            root.insert("metricModelsPopulated", metricModelsPopulated);
            root.insert("minimumMetricPoll", minimumMetricPoll);
            root.insert("stableMetricSamples", m_stableStandaloneMetricSamples);
            root.insert("metricsReady", metricsReady);
            root.insert("status", metricsReady ? "complete" : "loading");
            if (QAbstractItemView* eventView = FindEventView(application)) {
                root.insert("currentSelection", IndexSummary(
                    eventView->model(), eventView->currentIndex()));
            }
        }

        if (application != nullptr
            && (m_mode == "model-catalog" || m_mode == "model-export")) {
            const int currentModelCount = DiscoverModels(application).size();
            if (currentModelCount > 0 && currentModelCount == m_lastModelCount) {
                ++m_stableModelCountSamples;
            } else {
                m_stableModelCountSamples = 0;
            }
            m_lastModelCount = currentModelCount;
            int minimumModelPoll = qEnvironmentVariableIntValue(
                "NSIGHT_SOLID_PROBE_MODEL_MIN_POLL");
            minimumModelPoll = minimumModelPoll > 0 ? minimumModelPoll : 10;
            modelModeReady = FindEventView(application) != nullptr
                && m_stableModelCountSamples >= 1
                && m_pollCount >= minimumModelPoll;

            root.insert("schema", m_mode == "model-export"
                ? "NsightSolidProbeModelExportV1"
                : "NsightSolidProbeModelCatalogV1");
            root.insert("discoveredModelCount", currentModelCount);
            root.insert("stableModelCountSamples", m_stableModelCountSamples);
            root.insert("minimumModelPoll", minimumModelPoll);
            root.insert("modelsReady", modelModeReady);
            root.insert("status", modelModeReady ? "complete" : "loading");
            if (modelModeReady) {
                int modelTotal = 0;
                bool missingRequiredFilter = false;
                const QJsonArray models = CollectModels(
                    application,
                    m_mode == "model-export",
                    &modelTotal,
                    &missingRequiredFilter);
                root.insert("modelTotal", modelTotal);
                root.insert("modelReturned", models.size());
                root.insert("models", models);
                root.insert("missingRequiredFilter", missingRequiredFilter);
                root.insert("status", missingRequiredFilter ? "error" : "complete");
                if (missingRequiredFilter) {
                    root.insert(
                        "error",
                        "model-export requires MODEL_CLASS_MATCH or MODEL_OBJECT_MATCH");
                }
            }
        }

        Write(root);

        const bool requestReady = (m_mode == "heartbeat" && m_pollCount >= 10)
            || (m_mode == "event-discovery" && eventListReady)
            || (m_mode == "event-export" && eventExportReady)
            || ((m_mode == "metrics-discovery" || m_mode == "metrics-export") && metricsReady)
            || ((m_mode == "model-catalog" || m_mode == "model-export") && modelModeReady);
        if (requestReady || m_pollCount >= 120) {
            Finish(requestReady);
            const bool quitAfterHeartbeat = m_mode == "heartbeat"
                && qEnvironmentVariableIntValue("NSIGHT_SOLID_PROBE_QUIT_AFTER_HEARTBEAT") == 1;
            const bool quitWhenReady = ((m_mode == "event-discovery" && eventListReady)
                || (m_mode == "event-export" && eventExportReady))
                && qEnvironmentVariableIntValue("NSIGHT_SOLID_PROBE_QUIT_WHEN_READY") == 1;
            const bool quitWhenMetricsReady = (m_mode == "metrics-discovery" || m_mode == "metrics-export")
                && metricsReady
                && qEnvironmentVariableIntValue("NSIGHT_SOLID_PROBE_QUIT_WHEN_READY") == 1;
            const bool quitWhenModelsReady = (m_mode == "model-catalog" || m_mode == "model-export")
                && modelModeReady
                && qEnvironmentVariableIntValue("NSIGHT_SOLID_PROBE_QUIT_WHEN_READY") == 1;
            if (quitAfterHeartbeat || quitWhenReady || quitWhenMetricsReady || quitWhenModelsReady) {
                QTimer::singleShot(0, QCoreApplication::instance(), &QCoreApplication::quit);
            }
        }
    }

private:
    void Finish(bool reusable)
    {
        if (m_finished) {
            return;
        }
        m_finished = true;
        m_timer.stop();
        emit Finished(reusable);
    }

    void HandleDialogAutomation(QApplication* application, QJsonObject& root)
    {
        const QString configuredPath = qEnvironmentVariable(
            "NSIGHT_SOLID_PROBE_DIALOG_AUTO_PATH").trimmed();
        if (configuredPath.isEmpty()) {
            return;
        }
        root.insert("dialogAutoPath", configuredPath);
        root.insert("dialogSeen", m_dialogSeen);
        root.insert("dialogAcceptQueued", m_dialogAcceptQueued);
        root.insert("dialogSelectedFiles", m_dialogSelectedFiles);

        QSet<QFileDialog*> unique;
        for (QFileDialog* dialog : application->findChildren<QFileDialog*>()) {
            if (dialog != nullptr) {
                unique.insert(dialog);
            }
        }
        for (QWidget* widget : application->allWidgets()) {
            if (auto* dialog = qobject_cast<QFileDialog*>(widget)) {
                unique.insert(dialog);
            }
        }
        QFileDialog* targetDialog = nullptr;
        for (QFileDialog* dialog : unique) {
            if (dialog->isVisible()) {
                targetDialog = dialog;
                break;
            }
            if (targetDialog == nullptr) {
                targetDialog = dialog;
            }
        }
        if (targetDialog == nullptr) {
            return;
        }

        m_dialogSeen = true;
        root.insert("dialogSeen", true);
        root.insert("dialogClass", targetDialog->metaObject()->className());
        root.insert("dialogWindowTitle", targetDialog->windowTitle());
        root.insert("dialogFileMode", static_cast<int>(targetDialog->fileMode()));
        root.insert("dialogAcceptMode", static_cast<int>(targetDialog->acceptMode()));
        if (m_dialogAcceptQueued) {
            return;
        }

        targetDialog->setOption(QFileDialog::DontUseNativeDialog, true);
        const QFileInfo targetInfo(configuredPath);
        if (targetDialog->fileMode() == QFileDialog::Directory) {
            targetDialog->setDirectory(configuredPath);
        } else {
            targetDialog->setDirectory(targetInfo.absolutePath());
            targetDialog->selectFile(targetInfo.fileName());
        }
        m_dialogSelectedFiles = QJsonArray::fromStringList(
            targetDialog->selectedFiles());
        m_dialogAcceptQueued = QMetaObject::invokeMethod(
            targetDialog, "accept", Qt::QueuedConnection);
        root.insert("dialogAcceptQueued", m_dialogAcceptQueued);
        root.insert("dialogSelectedFiles", m_dialogSelectedFiles);
    }

    bool HandleSelectionMetrics(QApplication* application, QJsonObject& root)
    {
        root.insert("schema", "NsightSolidProbeSelectionMetricsV1");
        if (!m_eventSelector.isEmpty()) {
            root.insert("eventSelector", m_eventSelector);
        }
        if (!m_modelSelector.isEmpty()) {
            root.insert("modelSelector", m_modelSelector);
        }

        QAbstractItemView* eventView = FindEventView(application);

        if (eventView == nullptr || eventView->model() == nullptr) {
            root.insert("stage", "waiting-event-list");
            return false;
        }

        QAbstractItemModel* eventModel = eventView->model();
        root.insert("eventModelClass", eventModel->metaObject()->className());
        root.insert("currentSelection", IndexSummary(eventModel, eventView->currentIndex()));

        if (m_selectionAppliedPoll == 0) {
            int visited = 0;
            QJsonObject selector;
            const QModelIndex target = ResolveEventIndex(eventModel, &selector, &visited);
            m_eventSelector = selector;
            root.insert("eventNodesVisited", visited);
            root.insert("eventSelector", m_eventSelector);
            if (!target.isValid()) {
                if (visited == m_lastEventSearchVisited) {
                    ++m_stableEventSearchSamples;
                } else {
                    m_stableEventSearchSamples = 0;
                }
                m_lastEventSearchVisited = visited;
                root.insert("stableEventSearchSamples", m_stableEventSearchSamples);
                const bool invalidPath = m_eventSelector.value("kind").toString() == "path"
                    && !m_eventSelector.value("validPath").toBool()
                    && eventModel->rowCount() > 0;
                const bool stableSearchExhausted = visited > 0
                    && m_stableEventSearchSamples >= 1;
                if (invalidPath || stableSearchExhausted) {
                    root.insert("status", "error");
                    root.insert("stage", "event-target-not-found");
                    root.insert("searchExhausted", true);
                    return true;
                }
                root.insert("stage", "waiting-target-event");
                return false;
            }

            bool baselineReady = false;
            int baselineTotal = 0;
            const QJsonArray baselineCatalog = CollectMetricViews(
                application, false, &baselineReady, &baselineTotal);
            int baselineMinimumCount = qEnvironmentVariableIntValue(
                "NSIGHT_SOLID_PROBE_SELECTION_BASELINE_METRIC_MIN_COUNT");
            baselineMinimumCount = baselineMinimumCount > 0
                ? baselineMinimumCount
                : 1;
            int baselineStableMinimumPolls = qEnvironmentVariableIntValue(
                "NSIGHT_SOLID_PROBE_SELECTION_BASELINE_METRIC_STABLE_MIN_POLLS");
            baselineStableMinimumPolls = qMax(0, baselineStableMinimumPolls);
            if (baselineTotal > 0 && baselineTotal == m_lastBaselineMetricTotal) {
                ++m_stableBaselineMetricCountSamples;
            } else {
                m_stableBaselineMetricCountSamples = 0;
            }
            m_lastBaselineMetricTotal = baselineTotal;
            const bool baselineCatalogReady = baselineReady
                && baselineTotal >= baselineMinimumCount
                && m_stableBaselineMetricCountSamples >= baselineStableMinimumPolls;
            root.insert("metricViewTotal", baselineTotal);
            root.insert("metricViewReturned", baselineCatalog.size());
            root.insert("selectionBaselineMetricMinimumCount", baselineMinimumCount);
            root.insert("selectionBaselineMetricStableMinimumPolls",
                baselineStableMinimumPolls);
            root.insert("stableBaselineMetricCountSamples",
                m_stableBaselineMetricCountSamples);
            root.insert("baselineMetricCatalogReady", baselineCatalogReady);
            if (!baselineCatalogReady) {
                root.insert("stage", "waiting-baseline-metrics");
                return false;
            }

            const QJsonArray baseline = CollectMetricViews(
                application, true, &baselineReady, &baselineTotal);
            if (!baselineReady || baselineTotal < baselineMinimumCount) {
                root.insert("stage", "waiting-baseline-metrics-data");
                return false;
            }
            if (eventView->selectionModel() == nullptr) {
                root.insert("status", "error");
                root.insert("stage", "missing-selection-model");
                return true;
            }

            m_baselineMetricViews = baseline;
            const bool captureModelBaseline = HasModelExportFilters()
                && qEnvironmentVariableIntValue(
                    "NSIGHT_SOLID_PROBE_MODEL_CAPTURE_BASELINE") == 1;
            root.insert("modelBaselineRequested", captureModelBaseline);
            if (captureModelBaseline) {
                bool missingRequiredFilter = false;
                int baselineModelTotal = 0;
                m_baselineModels = CollectModels(
                    application,
                    true,
                    &baselineModelTotal,
                    &missingRequiredFilter);
                root.insert("baselineModelTotal", baselineModelTotal);
                root.insert("baselineModelReturned", m_baselineModels.size());
                m_modelBaselineCaptured = true;
            }
            m_beforeSelection = IndexSummary(eventModel, eventView->currentIndex());
            m_targetSelection = IndexSummary(eventModel, target);
            eventView->selectionModel()->setCurrentIndex(
                target,
                QItemSelectionModel::ClearAndSelect | QItemSelectionModel::Rows);
            m_selectionAppliedPoll = m_pollCount;

            root.insert("status", "selection-applied");
            root.insert("stage", "waiting-metrics-update");
            root.insert("beforeSelection", m_beforeSelection);
            root.insert("targetSelection", m_targetSelection);
            root.insert("currentSelection", IndexSummary(eventModel, eventView->currentIndex()));
            return false;
        }

        root.insert("beforeSelection", m_beforeSelection);
        root.insert("targetSelection", m_targetSelection);
        const int pollsSinceSelection = m_pollCount - m_selectionAppliedPoll;
        root.insert("pollsSinceSelection", pollsSinceSelection);
        const QJsonObject currentSelection = IndexSummary(eventModel, eventView->currentIndex());
        const bool selectionMatchesTarget = QJsonDocument(currentSelection)
            .toJson(QJsonDocument::Compact)
            == QJsonDocument(m_targetSelection).toJson(QJsonDocument::Compact);
        root.insert("selectionMatchesTarget", selectionMatchesTarget);

        int minimumSettlePolls = qEnvironmentVariableIntValue(
            "NSIGHT_SOLID_PROBE_METRIC_SETTLE_MIN_POLLS");
        int maximumSettlePolls = qEnvironmentVariableIntValue(
            "NSIGHT_SOLID_PROBE_METRIC_SETTLE_MAX_POLLS");
        int minimumModelSettlePolls = qEnvironmentVariableIntValue(
            "NSIGHT_SOLID_PROBE_MODEL_SETTLE_MIN_POLLS");
        minimumSettlePolls = minimumSettlePolls > 0 ? minimumSettlePolls : 4;
        maximumSettlePolls = maximumSettlePolls > 0 ? maximumSettlePolls : 20;
        minimumModelSettlePolls = minimumModelSettlePolls > 0
            ? minimumModelSettlePolls
            : 12;
        maximumSettlePolls = qMax(minimumSettlePolls + 1, maximumSettlePolls);
        if (HasModelExportFilters()) {
            maximumSettlePolls = qMax(minimumModelSettlePolls + 2, maximumSettlePolls);
        }
        if (HasModelRowSelector()
            || !qEnvironmentVariable("NSIGHT_SOLID_PROBE_ACTIVATE_PANEL").trimmed().isEmpty()
            || !qEnvironmentVariable(
                "NSIGHT_SOLID_PROBE_ACTION_TRIGGER_TEXT_MATCH").trimmed().isEmpty()) {
            maximumSettlePolls = qMax(40, maximumSettlePolls);
        }
        root.insert("minimumSettlePolls", minimumSettlePolls);
        root.insert("maximumSettlePolls", maximumSettlePolls);
        if (HasModelExportFilters()) {
            root.insert("minimumModelSettlePolls", minimumModelSettlePolls);
        }

        if (pollsSinceSelection < minimumSettlePolls || !selectionMatchesTarget) {
            root.insert("status", "selection-applied");
            root.insert("stage", selectionMatchesTarget
                ? "waiting-metrics-update"
                : "waiting-selection-current");
            return false;
        }

        bool metricsReady = false;
        int metricViewTotal = 0;
        const QJsonArray currentMetricViews = CollectMetricViews(
            application, true, &metricsReady, &metricViewTotal);
        if (!metricsReady) {
            root.insert("stage", "waiting-selected-metrics");
            return false;
        }

        if (HasModelExportFilters() && pollsSinceSelection < minimumModelSettlePolls) {
            root.insert("status", "selection-applied");
            root.insert("stage", "waiting-models-update");
            return false;
        }

        if (HasModelRowSelector()) {
            int modelSelectionSettlePolls = qEnvironmentVariableIntValue(
                "NSIGHT_SOLID_PROBE_MODEL_SELECT_SETTLE_MIN_POLLS");
            modelSelectionSettlePolls = modelSelectionSettlePolls > 0
                ? modelSelectionSettlePolls
                : 6;
            root.insert("minimumModelSelectionSettlePolls", modelSelectionSettlePolls);
            const QString modelSelectionTrigger = qEnvironmentVariable(
                "NSIGHT_SOLID_PROBE_MODEL_SELECT_TRIGGER").trimmed();
            const bool hasModelSelectionTriggerColumn = qEnvironmentVariableIsSet(
                "NSIGHT_SOLID_PROBE_MODEL_SELECT_TRIGGER_COLUMN");
            const int modelSelectionTriggerColumn = qMax(0,
                qEnvironmentVariableIntValue(
                    "NSIGHT_SOLID_PROBE_MODEL_SELECT_TRIGGER_COLUMN"));
            const bool hasModelSelectionTriggerXOffset = qEnvironmentVariableIsSet(
                "NSIGHT_SOLID_PROBE_MODEL_SELECT_TRIGGER_X_OFFSET");
            const int modelSelectionTriggerXOffset = qMax(0,
                qEnvironmentVariableIntValue(
                    "NSIGHT_SOLID_PROBE_MODEL_SELECT_TRIGGER_X_OFFSET"));
            root.insert("modelSelectionTrigger", modelSelectionTrigger);
            root.insert("modelSelectionTriggerColumn",
                hasModelSelectionTriggerColumn ? modelSelectionTriggerColumn : -1);
            root.insert("modelSelectionTriggerXOffset",
                hasModelSelectionTriggerXOffset ? modelSelectionTriggerXOffset : -1);
            root.insert("modelSelectionTriggerInvoked", m_modelSelectionTriggerInvoked);
            root.insert("modelSelectionTriggerViewVisible",
                m_modelSelectionTriggerViewVisible);
            root.insert("modelSelectionTriggerCellRectValid",
                m_modelSelectionTriggerCellRectValid);
            root.insert("modelSelectionTriggerCellRect",
                m_modelSelectionTriggerCellRect);
            root.insert("modelSelectionAfterTrigger",
                m_modelSelectionAfterTrigger);
            const bool hasProviderSummaryColumn = qEnvironmentVariableIsSet(
                "NSIGHT_SOLID_PROBE_MODEL_SELECT_PROVIDER_SUMMARY_COLUMN");
            const bool hasProviderModelColumn = qEnvironmentVariableIsSet(
                "NSIGHT_SOLID_PROBE_MODEL_SELECT_PROVIDER_MODEL_COLUMN");
            const bool verifyProviderSum = hasProviderSummaryColumn
                && hasProviderModelColumn;
            const int providerSummaryColumn = qMax(0, qEnvironmentVariableIntValue(
                "NSIGHT_SOLID_PROBE_MODEL_SELECT_PROVIDER_SUMMARY_COLUMN"));
            const int providerModelColumn = qMax(0, qEnvironmentVariableIntValue(
                "NSIGHT_SOLID_PROBE_MODEL_SELECT_PROVIDER_MODEL_COLUMN"));
            root.insert("modelSelectionVerifyProviderSum", verifyProviderSum);
            root.insert("modelSelectionProviderSummaryColumn",
                verifyProviderSum ? providerSummaryColumn : -1);
            root.insert("modelSelectionProviderModelColumn",
                verifyProviderSum ? providerModelColumn : -1);
            root.insert("modelSelectionNudgeApplied", m_modelSelectionNudgeApplied);
            root.insert("modelSelectionPreflightHasCurrent",
                m_modelSelectionPreflightHasCurrent);
            root.insert("modelSelectionNudgeTarget", m_modelSelectionNudgeTarget);
            root.insert("modelSelectionNudgeProviderChanged",
                m_modelSelectionNudgeProviderChanged);
            root.insert("modelSelectionNudgeProviderPopulated",
                m_modelSelectionNudgeProviderPopulated);
            root.insert("modelSelectionNudgeProviderStableSamples",
                m_modelSelectionNudgeProviderStableSamples);
            root.insert("modelSelectionNudgeProviderReady",
                m_modelSelectionNudgeProviderReady);
            root.insert("modelSelectionNudgeProviderExpectedSum",
                m_modelSelectionNudgeProviderExpectedSum);
            root.insert("modelSelectionNudgeProviderActualSum",
                m_modelSelectionNudgeProviderActualSum);
            root.insert("modelSelectionNudgeProviderSumMatches",
                m_modelSelectionNudgeProviderSumMatches);
            root.insert("modelSelectionTargetProviderChanged",
                m_modelSelectionTargetProviderChanged);
            root.insert("modelSelectionTargetProviderPopulated",
                m_modelSelectionTargetProviderPopulated);
            root.insert("modelSelectionTargetProviderStableSamples",
                m_modelSelectionTargetProviderStableSamples);
            root.insert("modelSelectionTargetProviderReady",
                m_modelSelectionTargetProviderReady);
            root.insert("modelSelectionTargetProviderExpectedSum",
                m_modelSelectionTargetProviderExpectedSum);
            root.insert("modelSelectionTargetProviderActualSum",
                m_modelSelectionTargetProviderActualSum);
            root.insert("modelSelectionTargetProviderSumMatches",
                m_modelSelectionTargetProviderSumMatches);
            const QString modelSelectionInvokeClass = qEnvironmentVariable(
                "NSIGHT_SOLID_PROBE_MODEL_SELECT_INVOKE_CLASS",
                "NV::ShaderProfiler::UI::SummaryPage").trimmed();
            const QString modelSelectionInvokeMethod = qEnvironmentVariable(
                "NSIGHT_SOLID_PROBE_MODEL_SELECT_INVOKE_METHOD").trimmed();
            root.insert("modelSelectionInvokeClass", modelSelectionInvokeClass);
            root.insert("modelSelectionInvokeMethod", modelSelectionInvokeMethod);
            root.insert("modelSelectionInvokeSucceeded", m_modelSelectionInvokeSucceeded);
            root.insert("modelSelectionInvokeTargetFound",
                m_modelSelectionInvokeTargetFound);
            root.insert("modelSelectionInvokeMethodFound",
                m_modelSelectionInvokeMethodFound);
            root.insert("modelSelectionInvokeInternalPointerAvailable",
                m_modelSelectionInvokeInternalPointerAvailable);
            root.insert("modelSelectionInvokeInternalId",
                m_modelSelectionInvokeInternalId);
            root.insert("modelSelectionInvokeParameterType",
                m_modelSelectionInvokeParameterType);
            root.insert("modelSelectionInvokeSourceModelClass",
                m_modelSelectionInvokeSourceModelClass);
            const bool allowUnsafePointerInvoke = qEnvironmentVariableIntValue(
                "NSIGHT_SOLID_PROBE_MODEL_SELECT_INVOKE_ALLOW_UNSAFE_POINTER") == 1;
            root.insert("modelSelectionInvokeAllowUnsafePointer",
                allowUnsafePointerInvoke);

            const QString preparePanelName = qEnvironmentVariable(
                "NSIGHT_SOLID_PROBE_MODEL_SELECT_PREPARE_PANEL").trimmed();
            if (!preparePanelName.isEmpty() && m_modelSelectionAppliedPoll == 0) {
                int preparePanelSettlePolls = qEnvironmentVariableIntValue(
                    "NSIGHT_SOLID_PROBE_MODEL_SELECT_PREPARE_PANEL_SETTLE_MIN_POLLS");
                preparePanelSettlePolls = preparePanelSettlePolls > 0
                    ? preparePanelSettlePolls
                    : 4;
                root.insert("modelSelectionPreparePanel", preparePanelName);
                root.insert("minimumModelSelectionPreparePanelSettlePolls",
                    preparePanelSettlePolls);

                QWidget* preparePanel = FindNamedWidget(application, preparePanelName);
                auto* prepareOwner = preparePanel == nullptr
                    ? nullptr
                    : qobject_cast<QStackedWidget*>(preparePanel->parentWidget());
                const int preparePanelIndex = prepareOwner == nullptr
                    ? -1
                    : prepareOwner->indexOf(preparePanel);
                if (preparePanel == nullptr || prepareOwner == nullptr
                    || preparePanelIndex < 0) {
                    root.insert("status", "selection-applied");
                    root.insert("stage", "waiting-model-selection-prepare-panel");
                    return false;
                }

                QString prepareButtonName = preparePanelName;
                prepareButtonName.replace("FlatTabPanel_", "FlatTabButton_");
                auto* prepareButton = qobject_cast<QAbstractButton*>(
                    FindNamedWidget(application, prepareButtonName));
                root.insert("modelSelectionPreparePanelButton",
                    prepareButton == nullptr ? QString() : prepareButton->objectName());
                if (m_modelPreparePanelActivatedPoll == 0) {
                    if (prepareButton != nullptr) {
                        prepareButton->click();
                    }
                    if (prepareOwner->currentWidget() != preparePanel) {
                        prepareOwner->setCurrentWidget(preparePanel);
                    }
                    m_modelPreparePanelActivatedPoll = m_pollCount;
                    root.insert("status", "selection-applied");
                    root.insert("stage", "waiting-model-selection-prepare-panel-update");
                    return false;
                }

                const bool preparePanelMatches = prepareOwner->currentWidget()
                    == preparePanel;
                const int pollsSincePreparePanel = m_pollCount
                    - m_modelPreparePanelActivatedPoll;
                root.insert("modelSelectionPreparePanelMatches", preparePanelMatches);
                root.insert("pollsSinceModelSelectionPreparePanel",
                    pollsSincePreparePanel);
                if (!preparePanelMatches) {
                    if (prepareButton != nullptr) {
                        prepareButton->click();
                    }
                    if (prepareOwner->currentWidget() != preparePanel) {
                        prepareOwner->setCurrentWidget(preparePanel);
                    }
                    m_modelPreparePanelActivatedPoll = m_pollCount;
                    root.insert("status", "selection-applied");
                    root.insert("stage", "reactivated-model-selection-prepare-panel");
                    return false;
                }
                if (pollsSincePreparePanel < preparePanelSettlePolls) {
                    root.insert("status", "selection-applied");
                    root.insert("stage", "waiting-model-selection-prepare-panel-update");
                    return false;
                }
            }

            QAbstractItemView* modelView = FindModelSelectionView(application);
            if (modelView == nullptr || modelView->model() == nullptr) {
                root.insert("status", "selection-applied");
                root.insert("stage", "waiting-model-selection-view");
                return false;
            }
            QAbstractItemModel* selectionModel = modelView->model();
            root.insert("modelSelectionViewClass", modelView->metaObject()->className());
            root.insert("modelSelectionViewObjectName", modelView->objectName());
            root.insert("modelSelectionModelClass", selectionModel->metaObject()->className());

            if (m_modelSelectionAppliedPoll == 0) {
                int visited = 0;
                QJsonObject selector;
                const QModelIndex target = ResolveModelSelectionIndex(
                    selectionModel, &selector, &visited);
                m_modelSelector = selector;
                root.insert("modelSelector", m_modelSelector);
                root.insert("modelSelectionNodesVisited", visited);
                if (!target.isValid()) {
                    root.insert("status", "error");
                    root.insert("stage", "model-selection-target-not-found");
                    return true;
                }
                if (modelView->selectionModel() == nullptr) {
                    root.insert("status", "error");
                    root.insert("stage", "missing-model-selection-model");
                    return true;
                }

                if (m_beforeModelSelection.isEmpty()) {
                    m_beforeModelSelection = IndexSummary(
                        selectionModel, modelView->currentIndex());
                }
                m_targetModelSelection = IndexSummary(selectionModel, target);
                m_targetModelSelectionParent = IndexSummary(
                    selectionModel, target.parent());
                int nudgeSettlePolls = qEnvironmentVariableIntValue(
                    "NSIGHT_SOLID_PROBE_MODEL_SELECT_NUDGE_SETTLE_MIN_POLLS");
                nudgeSettlePolls = nudgeSettlePolls > 0 ? nudgeSettlePolls : 2;
                int providerStablePolls = qEnvironmentVariableIntValue(
                    "NSIGHT_SOLID_PROBE_MODEL_SELECT_PROVIDER_STABLE_MIN_POLLS");
                providerStablePolls = providerStablePolls > 0
                    ? providerStablePolls
                    : 2;
                root.insert("minimumModelSelectionNudgeSettlePolls",
                    nudgeSettlePolls);
                root.insert("minimumModelSelectionProviderStablePolls",
                    providerStablePolls);
                root.insert("modelSelectionNudgeApplied",
                    m_modelSelectionNudgeApplied);
                root.insert("modelSelectionNudgeTarget",
                    m_modelSelectionNudgeTarget);
                if (m_modelSelectionNudgeApplied) {
                    const QJsonObject currentPreflightSelection = IndexSummary(
                        selectionModel, modelView->currentIndex());
                    if (m_modelSelectionPreflightHasCurrent
                        && !SameIndexCellValue(
                            currentPreflightSelection,
                            m_modelSelectionNudgeTarget,
                            qMax(0, qEnvironmentVariableIntValue(
                                "NSIGHT_SOLID_PROBE_MODEL_SELECT_COLUMN")))) {
                        m_modelSelectionNudgeApplied = false;
                        m_modelSelectionNudgeProviderReady = false;
                        m_modelSelectionNudgeLastProviderSnapshot.clear();
                        m_modelSelectionNudgeProviderSnapshot.clear();
                        m_modelSelectionNudgeProviderStableSamples = 0;
                        root.insert("status", "model-selection-preflight-reset");
                        root.insert("stage",
                            "waiting-model-selection-preflight-current");
                        return false;
                    }
                    const int pollsSinceNudge = m_pollCount
                        - m_modelSelectionNudgePoll;
                    root.insert("pollsSinceModelSelectionNudge", pollsSinceNudge);
                    if (pollsSinceNudge < nudgeSettlePolls) {
                        root.insert("status", "model-selection-nudged");
                        root.insert("stage", "waiting-model-selection-nudge-update");
                        return false;
                    }
                    if (!m_modelSelectionNudgeProviderReady) {
                        int providerModelTotal = 0;
                        bool missingRequiredFilter = false;
                        const QJsonArray providerModels = CollectModels(
                            application,
                            true,
                            &providerModelTotal,
                            &missingRequiredFilter);
                        const QByteArray providerSnapshot = ModelsSnapshot(providerModels);
                        m_modelSelectionNudgeProviderPopulated =
                            HasPopulatedModel(providerModels);
                        m_modelSelectionNudgeProviderChanged =
                            providerSnapshot != m_modelSelectionNudgeInitialProviderSnapshot;
                        const bool hasActualProviderSum = verifyProviderSum
                            && ModelsColumnSum(
                                providerModels,
                                providerModelColumn,
                                &m_modelSelectionNudgeProviderActualSum);
                        m_modelSelectionNudgeProviderSumMatches = verifyProviderSum
                            && m_modelSelectionNudgeProviderHasExpectedSum
                            && hasActualProviderSum
                            && m_modelSelectionNudgeProviderExpectedSum
                                == m_modelSelectionNudgeProviderActualSum;
                        if (!m_modelSelectionNudgeLastProviderSnapshot.isEmpty()
                            && providerSnapshot
                                == m_modelSelectionNudgeLastProviderSnapshot) {
                            ++m_modelSelectionNudgeProviderStableSamples;
                        } else {
                            m_modelSelectionNudgeProviderStableSamples = 0;
                        }
                        m_modelSelectionNudgeLastProviderSnapshot = providerSnapshot;
                        m_modelSelectionNudgeProviderReady =
                            m_modelSelectionNudgeProviderPopulated
                            && m_modelSelectionNudgeProviderStableSamples
                                >= providerStablePolls;
                        root.insert("modelSelectionNudgeProviderModelTotal",
                            providerModelTotal);
                        root.insert("modelSelectionNudgeProviderChanged",
                            m_modelSelectionNudgeProviderChanged);
                        root.insert("modelSelectionNudgeProviderPopulated",
                            m_modelSelectionNudgeProviderPopulated);
                        root.insert("modelSelectionNudgeProviderStableSamples",
                            m_modelSelectionNudgeProviderStableSamples);
                        root.insert("modelSelectionNudgeProviderReady",
                            m_modelSelectionNudgeProviderReady);
                        root.insert("modelSelectionNudgeProviderExpectedSum",
                            m_modelSelectionNudgeProviderExpectedSum);
                        root.insert("modelSelectionNudgeProviderActualSum",
                            m_modelSelectionNudgeProviderActualSum);
                        root.insert("modelSelectionNudgeProviderSumMatches",
                            m_modelSelectionNudgeProviderSumMatches);
                        if (!m_modelSelectionNudgeProviderReady) {
                            root.insert("status", "model-selection-preflight");
                            root.insert("stage",
                                "waiting-model-selection-preflight-provider");
                            return false;
                        }
                        m_modelSelectionNudgeProviderSnapshot = providerSnapshot;
                    }
                } else {
                    const QModelIndex preflightTarget = modelView->currentIndex();
                    if (!preflightTarget.isValid()
                        || selectionModel->flags(preflightTarget).testFlag(
                            Qt::ItemIsSelectable)) {
                        int providerModelTotal = 0;
                        bool missingRequiredFilter = false;
                        const QJsonArray initialProviderModels = CollectModels(
                            application,
                            true,
                            &providerModelTotal,
                            &missingRequiredFilter);
                        m_modelSelectionNudgeInitialProviderSnapshot =
                            ModelsSnapshot(initialProviderModels);
                        m_modelSelectionNudgeLastProviderSnapshot.clear();
                        m_modelSelectionNudgeProviderSnapshot.clear();
                        m_modelSelectionNudgeProviderStableSamples = 0;
                        m_modelSelectionNudgeProviderChanged = false;
                        m_modelSelectionNudgeProviderPopulated = false;
                        m_modelSelectionNudgeProviderReady = false;
                        m_modelSelectionNudgeProviderExpectedSum = 0;
                        m_modelSelectionNudgeProviderActualSum = 0;
                        m_modelSelectionNudgeProviderHasExpectedSum = false;
                        m_modelSelectionNudgeProviderSumMatches = false;
                        m_modelSelectionTargetLastProviderSnapshot.clear();
                        m_modelSelectionTargetProviderStableSamples = 0;
                        m_modelSelectionTargetProviderChanged = false;
                        m_modelSelectionTargetProviderPopulated = false;
                        m_modelSelectionTargetProviderReady = false;
                        m_modelSelectionNudgeApplied = true;
                        m_modelSelectionPreflightHasCurrent =
                            preflightTarget.isValid();
                        m_modelSelectionNudgePoll = m_pollCount;
                        m_modelSelectionNudgeTarget = preflightTarget.isValid()
                            ? IndexSummary(selectionModel, preflightTarget)
                            : QJsonObject{};
                        m_modelSelectionNudgeProviderHasExpectedSum =
                            verifyProviderSum
                            && IndexSummaryColumnSum(
                                m_modelSelectionNudgeTarget,
                                providerSummaryColumn,
                                &m_modelSelectionNudgeProviderExpectedSum);
                        m_modelSelectionTargetProviderExpectedSum = 0;
                        m_modelSelectionTargetProviderActualSum = 0;
                        m_modelSelectionTargetProviderHasExpectedSum =
                            verifyProviderSum
                            && IndexSummaryColumnSum(
                                m_targetModelSelection,
                                providerSummaryColumn,
                                &m_modelSelectionTargetProviderExpectedSum);
                        m_modelSelectionTargetProviderSumMatches = false;
                        if (!m_modelSelectionPreflightHasCurrent) {
                            // A details provider cannot populate until its producer
                            // row is selected. Treat an empty current selection as the
                            // settled baseline, then verify the provider after applying
                            // the requested target selection.
                            m_modelSelectionNudgeProviderReady = true;
                            m_modelSelectionNudgeProviderSnapshot =
                                m_modelSelectionNudgeInitialProviderSnapshot;
                        }
                        root.insert("modelSelectionNudgeApplied", true);
                        root.insert("modelSelectionNudgeTarget",
                            m_modelSelectionNudgeTarget);
                        root.insert("status", "model-selection-preflight");
                        root.insert("stage",
                            "waiting-model-selection-preflight-provider");
                        return false;
                    }
                    root.insert("status", "selection-applied");
                    root.insert("stage", "waiting-model-selection-current");
                    return false;
                }
                if (SameIndexCellValue(
                        m_modelSelectionNudgeTarget,
                        m_targetModelSelection,
                        qMax(0, qEnvironmentVariableIntValue(
                            "NSIGHT_SOLID_PROBE_MODEL_SELECT_COLUMN")))
                    && (!verifyProviderSum
                        || m_modelSelectionNudgeProviderSumMatches)) {
                    m_modelSelectionTargetProviderChanged = true;
                    m_modelSelectionTargetProviderPopulated =
                        m_modelSelectionNudgeProviderPopulated;
                    m_modelSelectionTargetProviderReady =
                        m_modelSelectionNudgeProviderReady;
                    m_modelSelectionTargetProviderExpectedSum =
                        m_modelSelectionNudgeProviderExpectedSum;
                    m_modelSelectionTargetProviderActualSum =
                        m_modelSelectionNudgeProviderActualSum;
                    m_modelSelectionTargetProviderHasExpectedSum =
                        m_modelSelectionNudgeProviderHasExpectedSum;
                    m_modelSelectionTargetProviderSumMatches =
                        m_modelSelectionNudgeProviderSumMatches;
                }
                if (modelSelectionTrigger != "mouseClick") {
                    modelView->selectionModel()->setCurrentIndex(
                        target,
                        QItemSelectionModel::ClearAndSelect
                            | QItemSelectionModel::Rows);
                }
                if (modelSelectionTrigger == "activated"
                    || modelSelectionTrigger == "clicked"
                    || modelSelectionTrigger == "doubleClicked"
                    || modelSelectionTrigger == "mouseClick") {
                    const QModelIndex triggerTarget = hasModelSelectionTriggerColumn
                        ? target.siblingAtColumn(qMin(
                            modelSelectionTriggerColumn,
                            selectionModel->columnCount(target.parent()) - 1))
                        : target;
                    if (modelSelectionTrigger == "mouseClick") {
                        m_modelSelectionTriggerViewVisible = modelView->isVisible();
                        if (auto* treeView = qobject_cast<QTreeView*>(modelView)) {
                            QList<QModelIndex> ancestors;
                            QModelIndex ancestor = triggerTarget.parent();
                            while (ancestor.isValid()) {
                                ancestors.prepend(ancestor);
                                ancestor = ancestor.parent();
                            }
                            for (const QModelIndex& item : ancestors) {
                                treeView->expand(item);
                            }
                        }
                        modelView->scrollTo(
                            triggerTarget, QAbstractItemView::PositionAtCenter);
                        const QRect cellRect = modelView->visualRect(triggerTarget);
                        m_modelSelectionTriggerCellRectValid = cellRect.isValid();
                        m_modelSelectionTriggerCellRect = QJsonObject{
                            {"x", cellRect.x()},
                            {"y", cellRect.y()},
                            {"width", cellRect.width()},
                            {"height", cellRect.height()},
                        };
                        QWidget* viewport = modelView->viewport();
                        if (viewport != nullptr && cellRect.isValid()) {
                            const QPoint localPoint = hasModelSelectionTriggerXOffset
                                ? QPoint(
                                    cellRect.left() + qMin(
                                        modelSelectionTriggerXOffset,
                                        qMax(0, cellRect.width() - 1)),
                                    cellRect.center().y())
                                : cellRect.center();
                            const QPoint globalPoint = viewport->mapToGlobal(localPoint);
                            QMouseEvent press(
                                QEvent::MouseButtonPress,
                                QPointF(localPoint),
                                QPointF(globalPoint),
                                Qt::LeftButton,
                                Qt::LeftButton,
                                Qt::NoModifier);
                            QMouseEvent release(
                                QEvent::MouseButtonRelease,
                                QPointF(localPoint),
                                QPointF(globalPoint),
                                Qt::LeftButton,
                                Qt::NoButton,
                                Qt::NoModifier);
                            const bool pressAccepted = QCoreApplication::sendEvent(
                                viewport, &press);
                            const bool releaseAccepted = QCoreApplication::sendEvent(
                                viewport, &release);
                            m_modelSelectionTriggerInvoked = pressAccepted
                                && releaseAccepted;
                            m_modelSelectionAfterTrigger = IndexSummary(
                                selectionModel, modelView->currentIndex());
                        }
                    } else {
                        m_modelSelectionTriggerInvoked = QMetaObject::invokeMethod(
                            modelView,
                            modelSelectionTrigger.toLatin1().constData(),
                            Qt::DirectConnection,
                        Q_ARG(QModelIndex, triggerTarget));
                    }
                }
                if (modelSelectionTrigger == "mouseClick"
                    && !m_modelSelectionTriggerInvoked) {
                    modelView->selectionModel()->setCurrentIndex(
                        target,
                        QItemSelectionModel::ClearAndSelect
                            | QItemSelectionModel::Rows);
                }
                if (!modelSelectionInvokeMethod.isEmpty()) {
                    QModelIndex sourceTarget = target;
                    QAbstractItemModel* sourceModel = selectionModel;
                    while (auto* proxy = qobject_cast<QAbstractProxyModel*>(sourceModel)) {
                        if (proxy->sourceModel() == nullptr) {
                            break;
                        }
                        sourceTarget = proxy->mapToSource(sourceTarget);
                        sourceModel = proxy->sourceModel();
                    }
                    m_modelSelectionInvokeSourceModelClass = sourceModel == nullptr
                        ? QString()
                        : QString::fromLatin1(sourceModel->metaObject()->className());

                    QObject* invokeTarget = nullptr;
                    const auto widgets = application->allWidgets();
                    for (QWidget* widget : widgets) {
                        if (QString::fromLatin1(widget->metaObject()->className())
                            == modelSelectionInvokeClass) {
                            invokeTarget = widget;
                            break;
                        }
                    }
                    if (invokeTarget == nullptr) {
                        const auto topLevels = application->topLevelWidgets();
                        for (QWidget* topLevel : topLevels) {
                            const auto objects = topLevel->findChildren<QObject*>();
                            for (QObject* object : objects) {
                                if (QString::fromLatin1(
                                        object->metaObject()->className())
                                    == modelSelectionInvokeClass) {
                                    invokeTarget = object;
                                    break;
                                }
                            }
                            if (invokeTarget != nullptr) {
                                break;
                            }
                        }
                    }
                    m_modelSelectionInvokeTargetFound = invokeTarget != nullptr;
                    if (sourceTarget.isValid()) {
                        m_modelSelectionInvokeInternalPointerAvailable =
                            sourceTarget.internalPointer() != nullptr;
                        m_modelSelectionInvokeInternalId = QString::number(
                            sourceTarget.internalId(), 16);
                    }
                    if (invokeTarget != nullptr && sourceTarget.isValid()) {
                        const QMetaObject* metaObject = invokeTarget->metaObject();
                        for (int methodIndex = 0;
                             methodIndex < metaObject->methodCount();
                             ++methodIndex) {
                            const QMetaMethod method = metaObject->method(methodIndex);
                            if (QString::fromLatin1(method.name())
                                != modelSelectionInvokeMethod) {
                                continue;
                            }
                            if (method.parameterCount() == 0) {
                                m_modelSelectionInvokeMethodFound = true;
                                m_modelSelectionInvokeSucceeded = method.invoke(
                                    invokeTarget, Qt::DirectConnection);
                                break;
                            }
                            if (method.parameterCount() != 1
                                || !allowUnsafePointerInvoke) {
                                continue;
                            }
                            m_modelSelectionInvokeMethodFound = true;
                            const QByteArray parameterType = method.parameterTypeName(0);
                            void* argumentValue = sourceTarget.internalPointer();
                            m_modelSelectionInvokeParameterType =
                                QString::fromLatin1(parameterType);
                            if (argumentValue != nullptr) {
                                m_modelSelectionInvokeSucceeded = method.invoke(
                                    invokeTarget,
                                    Qt::DirectConnection,
                                    QGenericArgument(
                                        parameterType.constData(), &argumentValue));
                            }
                            break;
                        }
                    }
                }
                m_modelSelectionAppliedPoll = m_pollCount;
                m_lastMetricSnapshot.clear();
                m_stableMetricSamples = 0;

                root.insert("status", "model-selection-applied");
                root.insert("stage", "waiting-model-selection-update");
                root.insert("beforeModelSelection", m_beforeModelSelection);
                root.insert("targetModelSelection", m_targetModelSelection);
                root.insert("targetModelSelectionParent",
                    m_targetModelSelectionParent);
                root.insert("currentModelSelection", IndexSummary(
                    selectionModel, modelView->currentIndex()));
                return false;
            }

            root.insert("beforeModelSelection", m_beforeModelSelection);
            root.insert("targetModelSelection", m_targetModelSelection);
            root.insert("targetModelSelectionParent",
                m_targetModelSelectionParent);
            const int pollsSinceModelSelection = m_pollCount - m_modelSelectionAppliedPoll;
            root.insert("pollsSinceModelSelection", pollsSinceModelSelection);
            const QJsonObject currentModelSelection = IndexSummary(
                selectionModel, modelView->currentIndex());
            const bool modelSelectionMatchesTarget = SameIndexCellValue(
                    currentModelSelection,
                    m_targetModelSelection,
                    qMax(0, qEnvironmentVariableIntValue(
                        "NSIGHT_SOLID_PROBE_MODEL_SELECT_COLUMN")))
                || (modelSelectionTrigger == "mouseClick"
                    && m_modelSelectionTriggerInvoked
                    && m_modelSelectionTriggerCellRectValid);
            root.insert("currentModelSelection", currentModelSelection);
            root.insert("modelSelectionMatchesTarget", modelSelectionMatchesTarget);
            if (!modelSelectionMatchesTarget
                || pollsSinceModelSelection < modelSelectionSettlePolls) {
                root.insert("status", "model-selection-applied");
                root.insert("stage", modelSelectionMatchesTarget
                    ? "waiting-model-selection-update"
                    : "waiting-model-selection-current");
                return false;
            }
            const QString providerPanelName = qEnvironmentVariable(
                "NSIGHT_SOLID_PROBE_ACTIVATE_PANEL").trimmed();
            bool providerPanelReady = true;
            if (!providerPanelName.isEmpty()) {
                QWidget* providerPanel = FindNamedWidget(
                    application, providerPanelName);
                auto* providerPanelOwner = providerPanel == nullptr
                    ? nullptr
                    : qobject_cast<QStackedWidget*>(providerPanel->parentWidget());
                providerPanelReady = providerPanel != nullptr
                    && providerPanelOwner != nullptr
                    && providerPanelOwner->currentWidget() == providerPanel;
            }
            root.insert("modelSelectionProviderPanelReady", providerPanelReady);
            const bool deferProviderVerificationUntilCombo =
                qEnvironmentVariableIntValue(
                    "NSIGHT_SOLID_PROBE_MODEL_SELECT_DEFER_PROVIDER_VERIFY_UNTIL_COMBO")
                == 1;
            root.insert("modelSelectionDeferProviderVerificationUntilCombo",
                deferProviderVerificationUntilCombo);
            if (m_modelSelectionNudgeProviderReady
                && !m_modelSelectionTargetProviderReady
                && providerPanelReady
                && (!deferProviderVerificationUntilCombo
                    || m_comboSelectionAppliedPoll != 0)) {
                int providerStablePolls = qEnvironmentVariableIntValue(
                    "NSIGHT_SOLID_PROBE_MODEL_SELECT_PROVIDER_STABLE_MIN_POLLS");
                providerStablePolls = providerStablePolls > 0
                    ? providerStablePolls
                    : 2;
                int providerModelTotal = 0;
                bool missingRequiredFilter = false;
                const QJsonArray providerModels = CollectModels(
                    application,
                    true,
                    &providerModelTotal,
                    &missingRequiredFilter);
                const QByteArray providerSnapshot = ModelsSnapshot(providerModels);
                m_modelSelectionTargetProviderPopulated =
                    HasPopulatedModel(providerModels);
                m_modelSelectionTargetProviderChanged =
                    providerSnapshot != m_modelSelectionNudgeProviderSnapshot;
                const bool hasActualProviderSum = verifyProviderSum
                    && ModelsColumnSum(
                        providerModels,
                        providerModelColumn,
                        &m_modelSelectionTargetProviderActualSum);
                m_modelSelectionTargetProviderSumMatches = verifyProviderSum
                    && m_modelSelectionTargetProviderHasExpectedSum
                    && hasActualProviderSum
                    && m_modelSelectionTargetProviderExpectedSum
                        == m_modelSelectionTargetProviderActualSum;
                if (!m_modelSelectionTargetLastProviderSnapshot.isEmpty()
                    && providerSnapshot == m_modelSelectionTargetLastProviderSnapshot) {
                    ++m_modelSelectionTargetProviderStableSamples;
                } else {
                    m_modelSelectionTargetProviderStableSamples = 0;
                }
                m_modelSelectionTargetLastProviderSnapshot = providerSnapshot;
                m_modelSelectionTargetProviderReady =
                    m_modelSelectionTargetProviderPopulated
                    && m_modelSelectionTargetProviderChanged
                    && (!verifyProviderSum
                        || m_modelSelectionTargetProviderSumMatches)
                    && m_modelSelectionTargetProviderStableSamples
                        >= providerStablePolls;
                root.insert("modelSelectionTargetProviderModelTotal",
                    providerModelTotal);
                root.insert("modelSelectionTargetProviderChanged",
                    m_modelSelectionTargetProviderChanged);
                root.insert("modelSelectionTargetProviderPopulated",
                    m_modelSelectionTargetProviderPopulated);
                root.insert("modelSelectionTargetProviderStableSamples",
                    m_modelSelectionTargetProviderStableSamples);
                root.insert("modelSelectionTargetProviderReady",
                    m_modelSelectionTargetProviderReady);
                root.insert("modelSelectionTargetProviderExpectedSum",
                    m_modelSelectionTargetProviderExpectedSum);
                root.insert("modelSelectionTargetProviderActualSum",
                    m_modelSelectionTargetProviderActualSum);
                root.insert("modelSelectionTargetProviderSumMatches",
                    m_modelSelectionTargetProviderSumMatches);
                if (!m_modelSelectionTargetProviderReady) {
                    root.insert("status", "model-selection-applied");
                    root.insert("stage", "waiting-model-selection-target-provider");
                    return false;
                }
            }
        }

        const QString requestedPanel = qEnvironmentVariable(
            "NSIGHT_SOLID_PROBE_ACTIVATE_PANEL").trimmed();
        if (!requestedPanel.isEmpty()) {
            int panelSettlePolls = qEnvironmentVariableIntValue(
                "NSIGHT_SOLID_PROBE_PANEL_SETTLE_MIN_POLLS");
            panelSettlePolls = panelSettlePolls > 0 ? panelSettlePolls : 6;
            root.insert("requestedPanel", requestedPanel);
            root.insert("minimumPanelSettlePolls", panelSettlePolls);

            QWidget* panel = FindNamedWidget(application, requestedPanel);
            auto* owner = panel == nullptr
                ? nullptr
                : qobject_cast<QStackedWidget*>(panel->parentWidget());
            const int panelIndex = owner == nullptr ? -1 : owner->indexOf(panel);
            if (panel == nullptr || owner == nullptr || panelIndex < 0) {
                root.insert("status", "model-selection-applied");
                root.insert("stage", "waiting-requested-panel");
                return false;
            }

            root.insert("panelClass", panel->metaObject()->className());
            root.insert("panelIndex", panelIndex);
            root.insert("panelOwnerClass", owner->metaObject()->className());
            root.insert("panelOwnerObjectName", owner->objectName());
            const bool activateViaButton = qEnvironmentVariableIntValue(
                "NSIGHT_SOLID_PROBE_ACTIVATE_PANEL_VIA_BUTTON") == 1;
            QString panelButtonName = requestedPanel;
            panelButtonName.replace("FlatTabPanel_", "FlatTabButton_");
            auto* panelButton = qobject_cast<QAbstractButton*>(
                FindNamedWidget(application, panelButtonName));
            root.insert("activatePanelViaButton", activateViaButton);
            root.insert("panelButtonObjectName", panelButton == nullptr
                ? QString()
                : panelButton->objectName());
            if (m_panelActivatedPoll == 0) {
                QWidget* before = owner->currentWidget();
                m_beforePanelObjectName = before == nullptr ? QString() : before->objectName();
                m_beforePanelIndex = owner->currentIndex();
                if (activateViaButton && panelButton != nullptr) {
                    panelButton->click();
                }
                if (owner->currentWidget() != panel) {
                    owner->setCurrentWidget(panel);
                }
                m_panelActivatedPoll = m_pollCount;
                m_lastMetricSnapshot.clear();
                m_stableMetricSamples = 0;

                root.insert("beforePanelObjectName", m_beforePanelObjectName);
                root.insert("beforePanelIndex", m_beforePanelIndex);
                root.insert("currentPanelObjectName", owner->currentWidget() == nullptr
                    ? QString()
                    : owner->currentWidget()->objectName());
                root.insert("currentPanelIndex", owner->currentIndex());
                root.insert("status", "panel-activated");
                root.insert("stage", "waiting-panel-update");
                return false;
            }

            const bool panelActivationMatches = owner->currentWidget() == panel;
            const int pollsSincePanelActivation = m_pollCount - m_panelActivatedPoll;
            root.insert("beforePanelObjectName", m_beforePanelObjectName);
            root.insert("beforePanelIndex", m_beforePanelIndex);
            root.insert("currentPanelObjectName", owner->currentWidget() == nullptr
                ? QString()
                : owner->currentWidget()->objectName());
            root.insert("currentPanelIndex", owner->currentIndex());
            root.insert("panelActivationMatches", panelActivationMatches);
            root.insert("pollsSincePanelActivation", pollsSincePanelActivation);
            if (!panelActivationMatches || pollsSincePanelActivation < panelSettlePolls) {
                root.insert("status", "panel-activated");
                root.insert("stage", panelActivationMatches
                    ? "waiting-panel-update"
                    : "waiting-panel-current");
                return false;
            }
        }

        const QString actionTriggerText = qEnvironmentVariable(
            "NSIGHT_SOLID_PROBE_ACTION_TRIGGER_TEXT_MATCH").trimmed();
        if (!actionTriggerText.isEmpty()) {
            int actionSettlePolls = qEnvironmentVariableIntValue(
                "NSIGHT_SOLID_PROBE_ACTION_SETTLE_MIN_POLLS");
            actionSettlePolls = actionSettlePolls > 0 ? actionSettlePolls : 10;
            const int requestedOccurrence = qMax(0,
                qEnvironmentVariableIntValue(
                    "NSIGHT_SOLID_PROBE_ACTION_TRIGGER_OCCURRENCE"));
            const bool triggerAsynchronously = qEnvironmentVariableIntValue(
                "NSIGHT_SOLID_PROBE_ACTION_TRIGGER_ASYNC") == 1;
            root.insert("actionTriggerTextMatch", actionTriggerText);
            root.insert("actionTriggerOccurrence", requestedOccurrence);
            root.insert("minimumActionSettlePolls", actionSettlePolls);
            root.insert("actionTriggerAsync", triggerAsynchronously);

            if (m_actionTriggeredPoll == 0) {
                const QList<QAction*> actions = DiscoverActions(application);
                root.insert("actionTriggerCandidateCount", actions.size());
                if (requestedOccurrence >= actions.size()) {
                    root.insert("status", "error");
                    root.insert("stage", "action-trigger-target-not-found");
                    return true;
                }
                QAction* targetAction = actions.at(requestedOccurrence);
                m_targetAction = ActionSummary(targetAction);
                root.insert("targetAction", m_targetAction);
                if (!targetAction->isEnabled()) {
                    root.insert("status", "error");
                    root.insert("stage", "action-trigger-target-disabled");
                    return true;
                }
                m_actionTriggeredPoll = m_pollCount;
                m_lastMetricSnapshot.clear();
                m_stableMetricSamples = 0;
                if (triggerAsynchronously) {
                    QTimer::singleShot(0, targetAction, [targetAction] {
                        targetAction->trigger();
                    });
                } else {
                    targetAction->trigger();
                }
                root.insert("status", "action-triggered");
                root.insert("stage", "waiting-action-update");
                return false;
            }

            const int pollsSinceAction = m_pollCount - m_actionTriggeredPoll;
            root.insert("targetAction", m_targetAction);
            root.insert("pollsSinceActionTrigger", pollsSinceAction);
            if (pollsSinceAction < actionSettlePolls) {
                root.insert("status", "action-triggered");
                root.insert("stage", "waiting-action-update");
                return false;
            }
        }

        const QString tabSelectionMatch = qEnvironmentVariable(
            "NSIGHT_SOLID_PROBE_TAB_SELECT_MATCH").trimmed();
        if (!tabSelectionMatch.isEmpty()) {
            int tabSelectionSettlePolls = qEnvironmentVariableIntValue(
                "NSIGHT_SOLID_PROBE_TAB_SELECT_SETTLE_MIN_POLLS");
            tabSelectionSettlePolls = tabSelectionSettlePolls > 0
                ? tabSelectionSettlePolls
                : 10;
            const bool tabSelectionExact = qEnvironmentVariable(
                "NSIGHT_SOLID_PROBE_TAB_SELECT_MATCH_MODE")
                .compare("exact", Qt::CaseInsensitive) == 0;
            const int requestedOccurrence = qMax(0,
                qEnvironmentVariableIntValue(
                    "NSIGHT_SOLID_PROBE_TAB_SELECT_OCCURRENCE"));
            root.insert("tabSelectionMatch", tabSelectionMatch);
            root.insert("tabSelectionMatchMode",
                tabSelectionExact ? "exact" : "contains");
            root.insert("tabSelectionOccurrence", requestedOccurrence);
            root.insert("minimumTabSelectionSettlePolls",
                tabSelectionSettlePolls);

            if (m_tabSelectionAppliedPoll == 0) {
                const QList<QTabWidget*> tabWidgets = DiscoverTabWidgets(application);
                int matchOccurrence = 0;
                int itemsVisited = 0;
                QTabWidget* targetTabWidget = nullptr;
                int targetIndex = -1;
                for (QTabWidget* tabWidget : tabWidgets) {
                    for (int index = 0; index < tabWidget->count(); ++index) {
                        ++itemsVisited;
                        if (!TabItemMatches(
                                tabWidget,
                                index,
                                tabSelectionMatch,
                                tabSelectionExact)) {
                            continue;
                        }
                        if (matchOccurrence == requestedOccurrence) {
                            targetTabWidget = tabWidget;
                            targetIndex = index;
                            break;
                        }
                        ++matchOccurrence;
                    }
                    if (targetTabWidget != nullptr) {
                        break;
                    }
                }
                root.insert("tabSelectionWidgetsVisited", tabWidgets.size());
                root.insert("tabSelectionItemsVisited", itemsVisited);
                root.insert("tabSelectionMatchesSkipped", matchOccurrence);
                if (targetTabWidget == nullptr || targetIndex < 0) {
                    root.insert("status", "error");
                    root.insert("stage", "tab-selection-target-not-found");
                    return true;
                }
                if (!targetTabWidget->isTabEnabled(targetIndex)
                    || !targetTabWidget->isTabVisible(targetIndex)) {
                    root.insert("status", "error");
                    root.insert("stage", "tab-selection-target-unavailable");
                    return true;
                }
                m_beforeTabSelection = QJsonObject{
                    {"class", targetTabWidget->metaObject()->className()},
                    {"objectName", targetTabWidget->objectName()},
                    {"index", targetTabWidget->currentIndex()},
                    {"text", targetTabWidget->tabText(
                        targetTabWidget->currentIndex())},
                    {"count", targetTabWidget->count()},
                    {"ancestry", ObjectAncestry(targetTabWidget)},
                };
                m_tabSelectionWidget = targetTabWidget;
                m_tabSelectionTargetIndex = targetIndex;
                m_tabSelectionTargetText = targetTabWidget->tabText(targetIndex);
                targetTabWidget->setCurrentIndex(targetIndex);
                m_tabSelectionAppliedPoll = m_pollCount;
                m_lastMetricSnapshot.clear();
                m_stableMetricSamples = 0;
                root.insert("beforeTabSelection", m_beforeTabSelection);
                root.insert("targetTabSelection", QJsonObject{
                    {"index", m_tabSelectionTargetIndex},
                    {"text", m_tabSelectionTargetText},
                });
                root.insert("status", "tab-selection-applied");
                root.insert("stage", "waiting-tab-selection-update");
                return false;
            }

            if (m_tabSelectionWidget.isNull()) {
                root.insert("status", "error");
                root.insert("stage", "tab-selection-widget-destroyed");
                return true;
            }
            const int pollsSinceTabSelection = m_pollCount
                - m_tabSelectionAppliedPoll;
            const bool tabSelectionMatchesTarget =
                m_tabSelectionWidget->currentIndex() == m_tabSelectionTargetIndex
                && m_tabSelectionWidget->tabText(m_tabSelectionTargetIndex)
                    == m_tabSelectionTargetText;
            root.insert("beforeTabSelection", m_beforeTabSelection);
            root.insert("targetTabSelection", QJsonObject{
                {"index", m_tabSelectionTargetIndex},
                {"text", m_tabSelectionTargetText},
            });
            root.insert("currentTabSelection", QJsonObject{
                {"index", m_tabSelectionWidget->currentIndex()},
                {"text", m_tabSelectionWidget->tabText(
                    m_tabSelectionWidget->currentIndex())},
            });
            root.insert("tabSelectionMatchesTarget", tabSelectionMatchesTarget);
            root.insert("pollsSinceTabSelection", pollsSinceTabSelection);
            if (!tabSelectionMatchesTarget
                || pollsSinceTabSelection < tabSelectionSettlePolls) {
                root.insert("status", "tab-selection-applied");
                root.insert("stage", tabSelectionMatchesTarget
                    ? "waiting-tab-selection-update"
                    : "waiting-tab-selection-current");
                return false;
            }
        }

        const QString invokeMethodName = qEnvironmentVariable(
            "NSIGHT_SOLID_PROBE_INVOKE_METHOD").trimmed();
        if (!invokeMethodName.isEmpty()) {
            int invokeSettlePolls = qEnvironmentVariableIntValue(
                "NSIGHT_SOLID_PROBE_INVOKE_SETTLE_MIN_POLLS");
            invokeSettlePolls = invokeSettlePolls > 0 ? invokeSettlePolls : 20;
            const int requestedOccurrence = qMax(0,
                qEnvironmentVariableIntValue(
                    "NSIGHT_SOLID_PROBE_INVOKE_OCCURRENCE"));
            root.insert("invokeMethod", invokeMethodName);
            root.insert("invokeOccurrence", requestedOccurrence);
            root.insert("minimumInvokeSettlePolls", invokeSettlePolls);
            root.insert("invokeTargetCandidateCount", m_invokeTargetCandidateCount);
            root.insert("invokeTarget", m_invokeTarget);
            root.insert("invokeMethodSignature", m_invokeMethodSignature);
            root.insert("invokeScheduled", m_invokeScheduledPoll > 0);
            root.insert("invokeStarted", m_invokeStarted);
            root.insert("invokeCompleted", m_invokeCompleted);
            root.insert("invokeSucceeded", m_invokeSucceeded);

            if (m_invokeScheduledPoll == 0) {
                const QList<QObject*> targets = DiscoverInvokeTargets(application);
                m_invokeTargetCandidateCount = targets.size();
                root.insert("invokeTargetCandidateCount", targets.size());
                if (requestedOccurrence >= targets.size()) {
                    root.insert("status", "error");
                    root.insert("stage", "invoke-target-not-found");
                    return true;
                }
                QObject* target = targets.at(requestedOccurrence);
                m_invokeTarget = ObjectSummary(target);
                root.insert("invokeTarget", m_invokeTarget);
                QMetaMethod targetMethod;
                const QMetaObject* metaObject = target->metaObject();
                for (int index = 0; index < metaObject->methodCount(); ++index) {
                    const QMetaMethod method = metaObject->method(index);
                    if (method.parameterCount() == 0
                        && QString::fromLatin1(method.name()).compare(
                            invokeMethodName,
                            Qt::CaseInsensitive) == 0) {
                        targetMethod = method;
                        break;
                    }
                }
                if (!targetMethod.isValid()) {
                    root.insert("status", "error");
                    root.insert("stage", "invoke-zero-argument-method-not-found");
                    return true;
                }
                m_invokeMethodSignature = QString::fromLatin1(
                    targetMethod.methodSignature());
                m_invokeScheduledPoll = m_pollCount;
                root.insert("invokeMethodSignature", m_invokeMethodSignature);
                root.insert("invokeScheduled", true);
                QPointer<QObject> guardedTarget(target);
                QTimer::singleShot(0, this, [this, guardedTarget, targetMethod] {
                    m_invokeStarted = true;
                    if (guardedTarget.isNull()) {
                        m_invokeSucceeded = false;
                        m_invokeCompleted = true;
                        m_invokeCompletedPoll = m_pollCount;
                        return;
                    }
                    m_invokeSucceeded = targetMethod.invoke(
                        guardedTarget.data(), Qt::DirectConnection);
                    m_invokeCompleted = true;
                    m_invokeCompletedPoll = m_pollCount;
                });
                root.insert("status", "invoke-scheduled");
                root.insert("stage", "waiting-invoke-start");
                return false;
            }

            if (!m_invokeCompleted) {
                root.insert("status", "invoke-scheduled");
                root.insert("stage", m_invokeStarted
                    ? "waiting-invoke-completion"
                    : "waiting-invoke-start");
                return false;
            }
            if (!m_invokeSucceeded) {
                root.insert("status", "error");
                root.insert("stage", "invoke-failed");
                return true;
            }
            const int pollsSinceInvoke = m_pollCount - m_invokeCompletedPoll;
            root.insert("pollsSinceInvoke", pollsSinceInvoke);
            if (pollsSinceInvoke < invokeSettlePolls) {
                root.insert("status", "invoke-complete");
                root.insert("stage", "waiting-invoke-update");
                return false;
            }
        }

        QString comboSelectionMatch = qEnvironmentVariable(
            "NSIGHT_SOLID_PROBE_COMBO_SELECT_MATCH").trimmed();
        const QString comboSelectionFromModel = qEnvironmentVariable(
            "NSIGHT_SOLID_PROBE_COMBO_SELECT_FROM_MODEL_SELECTION")
            .trimmed().toLower();
        if (comboSelectionMatch.isEmpty()
            && comboSelectionFromModel == "shader-source") {
            const QJsonArray shaderCells =
                m_targetModelSelection.value("cells").toArray();
            const QJsonArray pipelineCells =
                m_targetModelSelectionParent.value("cells").toArray();
            const QString shaderName = shaderCells.size() > 2
                ? shaderCells.at(2).toString().trimmed()
                : QString();
            const QString pipelineName = pipelineCells.size() > 2
                ? pipelineCells.at(2).toString().trimmed()
                : QString();
            if (shaderName.isEmpty() || pipelineName.isEmpty()) {
                root.insert("status", "error");
                root.insert("stage",
                    "combo-selection-model-identity-unavailable");
                return true;
            }
            comboSelectionMatch = pipelineName + " - " + shaderName;
            root.insert("comboSelectionDerivedFromModelSelection", true);
            root.insert("comboSelectionDerivedShaderName", shaderName);
            root.insert("comboSelectionDerivedPipelineName", pipelineName);
        }
        if (!comboSelectionMatch.isEmpty()) {
            int comboSelectionSettlePolls = qEnvironmentVariableIntValue(
                "NSIGHT_SOLID_PROBE_COMBO_SELECT_SETTLE_MIN_POLLS");
            comboSelectionSettlePolls = comboSelectionSettlePolls > 0
                ? comboSelectionSettlePolls
                : 10;
            const bool comboSelectionExact = qEnvironmentVariable(
                "NSIGHT_SOLID_PROBE_COMBO_SELECT_MATCH_MODE")
                .compare("exact", Qt::CaseInsensitive) == 0;
            const int requestedMatchOccurrence = qMax(0,
                qEnvironmentVariableIntValue(
                    "NSIGHT_SOLID_PROBE_COMBO_SELECT_OCCURRENCE"));
            const QString comboSelectionTrigger = qEnvironmentVariable(
                "NSIGHT_SOLID_PROBE_COMBO_SELECT_TRIGGER", "activated")
                .trimmed().toLower();
            root.insert("comboSelectionMatch", comboSelectionMatch);
            root.insert("comboSelectionMatchMode",
                comboSelectionExact ? "exact" : "contains");
            root.insert("comboSelectionOccurrence", requestedMatchOccurrence);
            root.insert("comboSelectionTrigger", comboSelectionTrigger);
            root.insert("minimumComboSelectionSettlePolls",
                comboSelectionSettlePolls);
            root.insert("comboSelectionActivatedInvoked",
                m_comboSelectionActivatedInvoked);
            root.insert("comboSelectionTextActivatedInvoked",
                m_comboSelectionTextActivatedInvoked);

            if (m_comboSelectionAppliedPoll == 0) {
                const QList<QComboBox*> comboBoxes = DiscoverComboBoxes(application);
                int matchOccurrence = 0;
                int itemsVisited = 0;
                QComboBox* targetComboBox = nullptr;
                int targetIndex = -1;
                for (QComboBox* comboBox : comboBoxes) {
                    for (int index = 0; index < comboBox->count(); ++index) {
                        ++itemsVisited;
                        if (!ComboItemMatches(
                                comboBox,
                                index,
                                comboSelectionMatch,
                                comboSelectionExact)) {
                            continue;
                        }
                        if (matchOccurrence == requestedMatchOccurrence) {
                            targetComboBox = comboBox;
                            targetIndex = index;
                            break;
                        }
                        ++matchOccurrence;
                    }
                    if (targetComboBox != nullptr) {
                        break;
                    }
                }
                root.insert("comboSelectionCombosVisited", comboBoxes.size());
                root.insert("comboSelectionItemsVisited", itemsVisited);
                root.insert("comboSelectionMatchesSkipped", matchOccurrence);
                if (targetComboBox == nullptr || targetIndex < 0) {
                    root.insert("status", "error");
                    root.insert("stage", "combo-selection-target-not-found");
                    return true;
                }

                m_beforeComboSelection = QJsonObject{
                    {"class", targetComboBox->metaObject()->className()},
                    {"objectName", targetComboBox->objectName()},
                    {"index", targetComboBox->currentIndex()},
                    {"text", targetComboBox->currentText()},
                    {"count", targetComboBox->count()},
                    {"ancestry", ObjectAncestry(targetComboBox)},
                };
                m_comboSelectionBox = targetComboBox;
                m_comboSelectionTargetIndex = targetIndex;
                m_comboSelectionTargetText = targetComboBox->itemText(targetIndex);
                targetComboBox->setCurrentIndex(targetIndex);
                if (comboSelectionTrigger == "activated") {
                    m_comboSelectionActivatedInvoked = QMetaObject::invokeMethod(
                        targetComboBox,
                        "activated",
                        Qt::DirectConnection,
                        Q_ARG(int, targetIndex));
                    m_comboSelectionTextActivatedInvoked = QMetaObject::invokeMethod(
                        targetComboBox,
                        "textActivated",
                        Qt::DirectConnection,
                        Q_ARG(QString, m_comboSelectionTargetText));
                }
                m_comboSelectionAppliedPoll = m_pollCount;
                m_lastMetricSnapshot.clear();
                m_stableMetricSamples = 0;
                root.insert("beforeComboSelection", m_beforeComboSelection);
                root.insert("targetComboSelection", QJsonObject{
                    {"index", m_comboSelectionTargetIndex},
                    {"text", m_comboSelectionTargetText},
                });
                root.insert("currentComboSelection", QJsonObject{
                    {"index", targetComboBox->currentIndex()},
                    {"text", targetComboBox->currentText()},
                });
                root.insert("status", "combo-selection-applied");
                root.insert("stage", "waiting-combo-selection-update");
                return false;
            }

            if (m_comboSelectionBox.isNull()) {
                root.insert("status", "error");
                root.insert("stage", "combo-selection-widget-destroyed");
                return true;
            }
            const int pollsSinceComboSelection = m_pollCount
                - m_comboSelectionAppliedPoll;
            const bool comboSelectionMatchesTarget =
                m_comboSelectionBox->currentIndex() == m_comboSelectionTargetIndex
                && m_comboSelectionBox->currentText() == m_comboSelectionTargetText;
            root.insert("beforeComboSelection", m_beforeComboSelection);
            root.insert("targetComboSelection", QJsonObject{
                {"index", m_comboSelectionTargetIndex},
                {"text", m_comboSelectionTargetText},
            });
            root.insert("currentComboSelection", QJsonObject{
                {"index", m_comboSelectionBox->currentIndex()},
                {"text", m_comboSelectionBox->currentText()},
            });
            root.insert("comboSelectionMatchesTarget", comboSelectionMatchesTarget);
            root.insert("pollsSinceComboSelection", pollsSinceComboSelection);
            if (!comboSelectionMatchesTarget
                || pollsSinceComboSelection < comboSelectionSettlePolls) {
                root.insert("status", "combo-selection-applied");
                root.insert("stage", comboSelectionMatchesTarget
                    ? "waiting-combo-selection-update"
                    : "waiting-combo-selection-current");
                return false;
            }
        }

        QJsonArray currentModels;
        int currentModelTotal = 0;
        if (HasModelExportFilters()) {
            bool missingRequiredFilter = false;
            currentModels = CollectModels(
                application,
                true,
                &currentModelTotal,
                &missingRequiredFilter);
            const int requiredModelCount = qMax(0,
                qEnvironmentVariableIntValue(
                    "NSIGHT_SOLID_PROBE_MODEL_REQUIRED_MIN_COUNT"));
            const bool requiredModelsReady =
                currentModels.size() >= requiredModelCount;
            root.insert("requiredModelCount", requiredModelCount);
            root.insert("requiredModelsReady", requiredModelsReady);
            root.insert("currentModelTotal", currentModelTotal);
            root.insert("currentModelReturned", currentModels.size());
            if (!requiredModelsReady) {
                root.insert("status", "selection-applied");
                root.insert("stage", "waiting-required-models");
                return false;
            }
        }

        const QJsonObject combinedSnapshot{
            {"metricViews", currentMetricViews},
            {"models", currentModels},
        };
        const QByteArray currentSnapshot = QJsonDocument(combinedSnapshot)
            .toJson(QJsonDocument::Compact);
        if (!m_lastMetricSnapshot.isEmpty() && m_lastMetricSnapshot == currentSnapshot) {
            ++m_stableMetricSamples;
        } else {
            m_stableMetricSamples = 0;
        }
        m_lastMetricSnapshot = currentSnapshot;
        m_lastMetricViews = currentMetricViews;
        m_lastModels = currentModels;

        const bool metricsStable = m_stableMetricSamples >= 1;
        const bool settleTimedOut = pollsSinceSelection >= maximumSettlePolls;
        root.insert("stableMetricSamples", m_stableMetricSamples);
        root.insert("metricsStable", metricsStable);
        root.insert("settleTimedOut", settleTimedOut);
        if (!metricsStable && !settleTimedOut) {
            root.insert("status", "selection-applied");
            root.insert("stage", "waiting-metrics-stable");
            return false;
        }

        root.insert("baselineMetricViews", m_baselineMetricViews);
        root.insert("metricViews", m_lastMetricViews);
        root.insert("metricViewTotal", metricViewTotal);
        root.insert("metricViewReturned", m_lastMetricViews.size());
        root.insert("changedMetricViewCount", CountChangedMetricViews(
            m_baselineMetricViews, m_lastMetricViews));
        if (HasModelExportFilters()) {
            root.insert("models", m_lastModels);
            root.insert("modelTotal", currentModelTotal);
            root.insert("modelReturned", m_lastModels.size());
            root.insert("modelBaselineCaptured", m_modelBaselineCaptured);
            if (m_modelBaselineCaptured) {
                root.insert("baselineModels", m_baselineModels);
                root.insert("changedModelCount", CountChangedModels(
                    m_baselineModels, m_lastModels));
            }
        }
        if (HasComboProbe()) {
            int comboTotal = 0;
            const QJsonArray comboBoxes = CollectComboBoxes(application, &comboTotal);
            root.insert("comboTotal", comboTotal);
            root.insert("comboReturned", comboBoxes.size());
            root.insert("combos", comboBoxes);
        }
        if (HasTabProbe()) {
            int tabTotal = 0;
            const QJsonArray tabWidgets = CollectTabWidgets(application, &tabTotal);
            root.insert("tabTotal", tabTotal);
            root.insert("tabReturned", tabWidgets.size());
            root.insert("tabs", tabWidgets);
        }
        if (HasObjectProbe()) {
            int objectTotal = 0;
            const QJsonArray objects = CollectObjects(application, &objectTotal);
            root.insert("objectTotal", objectTotal);
            root.insert("objectReturned", objects.size());
            root.insert("objects", objects);
        }
        root.insert("status", "complete");
        root.insert("stage", "complete");
        return true;
    }

    void Write(const QJsonObject& value) const
    {
        if (m_outputPath.isEmpty()) {
            return;
        }

        QSaveFile file(m_outputPath);
        if (!file.open(QIODevice::WriteOnly)) {
            return;
        }
        file.write(QJsonDocument(value).toJson(QJsonDocument::Indented));
        file.commit();
    }

    QString m_outputPath;
    QString m_mode;
    QTimer m_timer;
    int m_pollCount = 0;
    int m_selectionAppliedPoll = 0;
    int m_modelSelectionAppliedPoll = 0;
    int m_modelPreparePanelActivatedPoll = 0;
    int m_panelActivatedPoll = 0;
    int m_comboSelectionAppliedPoll = 0;
    int m_actionTriggeredPoll = 0;
    int m_tabSelectionAppliedPoll = 0;
    int m_invokeScheduledPoll = 0;
    int m_invokeCompletedPoll = 0;
    int m_invokeTargetCandidateCount = 0;
    int m_modelSelectionNudgePoll = 0;
    int m_comboSelectionTargetIndex = -1;
    int m_tabSelectionTargetIndex = -1;
    int m_beforePanelIndex = -1;
    int m_lastModelCount = 0;
    int m_stableModelCountSamples = 0;
    int m_stableMetricSamples = 0;
    int m_stableStandaloneMetricSamples = 0;
    int m_lastBaselineMetricTotal = -1;
    int m_stableBaselineMetricCountSamples = 0;
    int m_lastEventSearchVisited = -1;
    int m_modelSelectionNudgeProviderStableSamples = 0;
    int m_modelSelectionTargetProviderStableSamples = 0;
    int m_stableEventSearchSamples = 0;
    bool m_modelBaselineCaptured = false;
    bool m_modelSelectionTriggerInvoked = false;
    bool m_modelSelectionTriggerViewVisible = false;
    bool m_modelSelectionTriggerCellRectValid = false;
    bool m_modelSelectionInvokeSucceeded = false;
    bool m_modelSelectionInvokeTargetFound = false;
    bool m_modelSelectionInvokeMethodFound = false;
    bool m_modelSelectionInvokeInternalPointerAvailable = false;
    bool m_comboSelectionActivatedInvoked = false;
    bool m_comboSelectionTextActivatedInvoked = false;
    bool m_invokeStarted = false;
    bool m_invokeCompleted = false;
    bool m_invokeSucceeded = false;
    bool m_dialogSeen = false;
    bool m_dialogAcceptQueued = false;
    bool m_finished = false;
    bool m_modelSelectionNudgeApplied = false;
    bool m_modelSelectionPreflightHasCurrent = false;
    bool m_modelSelectionNudgeProviderChanged = false;
    bool m_modelSelectionNudgeProviderPopulated = false;
    bool m_modelSelectionNudgeProviderReady = false;
    bool m_modelSelectionNudgeProviderHasExpectedSum = false;
    bool m_modelSelectionNudgeProviderSumMatches = false;
    bool m_modelSelectionTargetProviderChanged = false;
    bool m_modelSelectionTargetProviderPopulated = false;
    bool m_modelSelectionTargetProviderReady = false;
    bool m_modelSelectionTargetProviderHasExpectedSum = false;
    bool m_modelSelectionTargetProviderSumMatches = false;
    QPointer<QComboBox> m_comboSelectionBox;
    QPointer<QTabWidget> m_tabSelectionWidget;
    QJsonArray m_baselineMetricViews;
    QJsonArray m_lastMetricViews;
    QJsonArray m_baselineModels;
    QJsonArray m_lastModels;
    QJsonObject m_beforeSelection;
    QJsonObject m_targetSelection;
    QJsonObject m_eventSelector;
    QJsonObject m_beforeModelSelection;
    QJsonObject m_targetModelSelection;
    QJsonObject m_modelSelector;
    QJsonObject m_beforeComboSelection;
    QJsonObject m_targetAction;
    QJsonObject m_beforeTabSelection;
    QJsonObject m_invokeTarget;
    QJsonObject m_modelSelectionNudgeTarget;
    QJsonObject m_modelSelectionTriggerCellRect;
    QJsonObject m_modelSelectionAfterTrigger;
    QJsonObject m_targetModelSelectionParent;
    QString m_beforePanelObjectName;
    QString m_modelSelectionInvokeParameterType;
    QString m_modelSelectionInvokeSourceModelClass;
    QString m_modelSelectionInvokeInternalId;
    QString m_comboSelectionTargetText;
    QString m_tabSelectionTargetText;
    QString m_invokeMethodSignature;
    QJsonArray m_dialogSelectedFiles;
    qint64 m_modelSelectionNudgeProviderExpectedSum = 0;
    qint64 m_modelSelectionNudgeProviderActualSum = 0;
    qint64 m_modelSelectionTargetProviderExpectedSum = 0;
    qint64 m_modelSelectionTargetProviderActualSum = 0;
    QByteArray m_lastMetricSnapshot;
    QByteArray m_lastStandaloneMetricSnapshot;
    QByteArray m_modelSelectionNudgeInitialProviderSnapshot;
    QByteArray m_modelSelectionNudgeLastProviderSnapshot;
    QByteArray m_modelSelectionNudgeProviderSnapshot;
    QByteArray m_modelSelectionTargetLastProviderSnapshot;
};

class SolidProbeSessionAgent final : public QObject
{
    Q_OBJECT

public:
    explicit SolidProbeSessionAgent(QObject* parent = nullptr)
        : QObject(parent)
        , m_sessionDirectory(QDir::cleanPath(QFileInfo(qEnvironmentVariable(
              "NSIGHT_SOLID_PROBE_SESSION_DIRECTORY")).absoluteFilePath()))
        , m_runRoot(QDir::cleanPath(QFileInfo(qEnvironmentVariable(
              "NSIGHT_SOLID_PROBE_SESSION_RUN_ROOT")).absoluteFilePath()))
        , m_sessionId(qEnvironmentVariable(
              "NSIGHT_SOLID_PROBE_SESSION_ID").trimmed())
        , m_reportId(qEnvironmentVariable(
              "NSIGHT_SOLID_PROBE_REPORT_ID").trimmed())
        , m_createdUtc(QDateTime::currentDateTimeUtc())
    {
        const int configuredIdleTimeout = qEnvironmentVariableIntValue(
            "NSIGHT_SOLID_PROBE_SESSION_IDLE_TIMEOUT_MS");
        m_idleTimeoutMs = configuredIdleTimeout > 0
            ? qBound(10000, configuredIdleTimeout, 3600000)
            : 300000;
        m_lastActivityMs = QDateTime::currentMSecsSinceEpoch();
        m_valid = !m_sessionId.isEmpty()
            && !m_reportId.isEmpty()
            && !m_sessionDirectory.isEmpty()
            && !m_runRoot.isEmpty()
            && QDir().mkpath(m_sessionDirectory)
            && QDir(m_runRoot).exists();
        m_state = m_valid ? "ready" : "poisoned";
        if (!m_valid) {
            m_protocolError = "invalid-session-configuration";
        }

        ClearProductRequestEnvironment();
        m_timer.setInterval(100);
        connect(&m_timer, &QTimer::timeout, this, &SolidProbeSessionAgent::Poll);
        QTimer::singleShot(0, this, [this] {
            WriteManifest();
            m_timer.start();
        });
    }

private slots:
    void Poll()
    {
        WriteManifest();

        if (!m_valid || m_agent != nullptr || m_state != "ready") {
            return;
        }

        const QString closePath = QDir(m_sessionDirectory).filePath("close.json");
        if (QFileInfo::exists(closePath)) {
            QJsonObject closeRequest;
            QString error;
            const bool validClose = ReadBoundedObject(
                closePath, 16 * 1024, &closeRequest, &error)
                && closeRequest.value("schema").toString()
                    == "NsightSolidProbeSessionCloseV1"
                && closeRequest.value("sessionId").toString() == m_sessionId;
            QFile::remove(closePath);
            if (!validClose) {
                Poison(error.isEmpty() ? "invalid-close-request" : error);
                return;
            }
            BeginShutdown("explicit");
            return;
        }

        const QString requestPath = QDir(m_sessionDirectory).filePath("request.json");
        if (QFileInfo::exists(requestPath)) {
            StartRequest(requestPath);
            return;
        }

        if (QDateTime::currentMSecsSinceEpoch() - m_lastActivityMs
            >= m_idleTimeoutMs) {
            BeginShutdown("idleTimeout");
        }
    }

    void RequestFinished(bool reusable)
    {
        if (m_agent != nullptr) {
            m_agent->deleteLater();
            m_agent = nullptr;
        }
        ClearProductRequestEnvironment();
        m_lastRequestId = m_currentRequestId;
        m_currentRequestId.clear();
        m_lastActivityMs = QDateTime::currentMSecsSinceEpoch();

        if (!reusable) {
            Poison("request-left-session-unsafe");
            return;
        }

        m_state = "cleaning";
        if (m_closeRequestWindows) {
            CloseRequestWindows();
        }
        m_closeRequestWindows = false;
        m_requestBaselineTopLevels.clear();
        WriteManifest();
        QTimer::singleShot(250, this, [this] {
            if (m_state != "cleaning") {
                return;
            }
            m_state = "ready";
            m_lastActivityMs = QDateTime::currentMSecsSinceEpoch();
            WriteManifest();
        });
    }

private:
    bool ReadBoundedObject(
        const QString& path,
        qint64 maximumBytes,
        QJsonObject* result,
        QString* error) const
    {
        QFile file(path);
        if (!file.open(QIODevice::ReadOnly)) {
            *error = "request-unreadable";
            return false;
        }
        if (file.size() <= 0 || file.size() > maximumBytes) {
            *error = "request-size-invalid";
            return false;
        }
        QJsonParseError parseError;
        const QJsonDocument document = QJsonDocument::fromJson(
            file.readAll(), &parseError);
        if (parseError.error != QJsonParseError::NoError || !document.isObject()) {
            *error = "request-json-invalid";
            return false;
        }
        *result = document.object();
        return true;
    }

    void StartRequest(const QString& requestPath)
    {
        QJsonObject request;
        QString error;
        const bool read = ReadBoundedObject(
            requestPath, 1024 * 1024, &request, &error);
        QFile::remove(requestPath);
        if (!read) {
            Poison(error);
            return;
        }

        const QString requestId = request.value("requestId").toString().trimmed();
        const QString reportId = request.value("reportId").toString();
        const QString sessionId = request.value("sessionId").toString();
        const QString mode = request.value("mode").toString().trimmed();
        const QString expectedSchema = request.value("expectedSchema").toString();
        const QString outputPath = QDir::cleanPath(QFileInfo(
            request.value("outputPath").toString()).absoluteFilePath());
        const QJsonValue settingsValue = request.value("settings");
        if (request.value("schema").toString() != kSessionRequestSchema) {
            Poison("request-schema-invalid");
            return;
        }
        if (sessionId != m_sessionId) {
            Poison("request-session-identity-invalid");
            return;
        }
        if (reportId != m_reportId) {
            Poison("request-report-identity-invalid");
            return;
        }
        if (requestId.size() != 32
            || !std::all_of(
                requestId.cbegin(), requestId.cend(), [](QChar character) {
                    return character.isDigit()
                        || (character >= 'a' && character <= 'f')
                        || (character >= 'A' && character <= 'F');
                })) {
            Poison("request-id-invalid");
            return;
        }
        if (!IsProductSessionMode(mode)
            || expectedSchema != ExpectedProductSchema(mode)) {
            Poison("request-operation-invalid");
            return;
        }
        if (QFileInfo(outputPath).fileName() != "bridge-output.json") {
            Poison("request-output-name-invalid");
            return;
        }
        if (!IsPathInside(outputPath, m_runRoot)) {
            Poison("request-output-root-invalid");
            return;
        }
        if (!QFileInfo(outputPath).absoluteDir().exists()) {
            Poison("request-output-directory-missing");
            return;
        }
        if (QFileInfo::exists(outputPath)) {
            Poison("request-output-not-fresh");
            return;
        }
        if (!settingsValue.isObject()) {
            Poison("request-settings-shape-invalid");
            return;
        }

        const QJsonObject settings = settingsValue.toObject();
        if (settings.size() > ProductSessionSettingNames().size()) {
            Poison("request-settings-count-invalid");
            return;
        }
        for (auto iterator = settings.constBegin(); iterator != settings.constEnd(); ++iterator) {
            if (!ProductSessionSettingNames().contains(iterator.key())
                || !iterator.value().isString()
                || iterator.value().toString().size() > 32768) {
                Poison("request-setting-invalid");
                return;
            }
        }

        ClearProductRequestEnvironment();
        qputenv("NSIGHT_SOLID_PROBE_OUTPUT", outputPath.toUtf8());
        qputenv("NSIGHT_SOLID_PROBE_MODE", mode.toUtf8());
        qputenv("NSIGHT_SOLID_PROBE_REQUEST_ID", requestId.toUtf8());
        // REPORT_ID is invariant for the session and was supplied through the
        // launch-time wide-character environment. Preserve it: qputenv's narrow
        // Windows path corrupts non-ASCII report names even when given UTF-8.
        for (auto iterator = settings.constBegin(); iterator != settings.constEnd(); ++iterator) {
            const QByteArray name = ("NSIGHT_SOLID_PROBE_" + iterator.key()).toUtf8();
            qputenv(name.constData(), iterator.value().toString().toUtf8());
        }

        m_currentRequestId = requestId;
        m_closeRequestWindows = settings.contains("ACTION_TRIGGER_TEXT_MATCH")
            || settings.contains("DIALOG_AUTO_PATH");
        if (auto* application = qobject_cast<QApplication*>(
                QCoreApplication::instance())) {
            for (QWidget* topLevel : application->topLevelWidgets()) {
                if (topLevel != nullptr) {
                    m_requestBaselineTopLevels.insert(topLevel);
                }
            }
        }
        m_state = "busy";
        m_protocolError.clear();
        m_lastActivityMs = QDateTime::currentMSecsSinceEpoch();
        WriteManifest();
        m_agent = new SolidProbeAgent(this);
        connect(
            m_agent,
            &SolidProbeAgent::Finished,
            this,
            &SolidProbeSessionAgent::RequestFinished,
            Qt::QueuedConnection);
    }

    void CloseRequestWindows()
    {
        auto* application = qobject_cast<QApplication*>(QCoreApplication::instance());
        if (application == nullptr) {
            return;
        }
        for (QWidget* topLevel : application->topLevelWidgets()) {
            if (topLevel != nullptr
                && !m_requestBaselineTopLevels.contains(topLevel)) {
                topLevel->close();
            }
        }
    }

    void Poison(const QString& error)
    {
        m_state = "poisoned";
        m_protocolError = error.left(256);
        m_lastActivityMs = QDateTime::currentMSecsSinceEpoch();
        WriteManifest();
    }

    void BeginShutdown(const QString& reason)
    {
        m_state = "closing";
        m_shutdownReason = reason;
        WriteManifest();
        m_timer.stop();
        QTimer::singleShot(0, QCoreApplication::instance(), &QCoreApplication::quit);
    }

    void WriteManifest() const
    {
        if (m_sessionDirectory.isEmpty()) {
            return;
        }
        auto* application = qobject_cast<QApplication*>(QCoreApplication::instance());
        QJsonObject manifest{
            {"schema", kSessionSchema},
            {"status", m_state},
            {"pluginVersion", kPluginVersion},
            {"sessionId", m_sessionId},
            {"reportId", m_reportId},
            {"pid", static_cast<qint64>(QCoreApplication::applicationPid())},
            {"qtRuntimeVersion", qVersion()},
            {"applicationVersion", QCoreApplication::applicationVersion()},
            {"verifiedHostTarget", QJsonObject{
                {"nsightVersion", kVerifiedNsightVersion},
                {"nsightBuild", kVerifiedNsightBuild},
            }},
            {"transport", "filesystemMailboxV1"},
            {"idleTimeoutMs", m_idleTimeoutMs},
            {"createdUtc", m_createdUtc.toString(Qt::ISODateWithMs)},
            {"lastActivityUtc", QDateTime::fromMSecsSinceEpoch(
                m_lastActivityMs, Qt::UTC).toString(Qt::ISODateWithMs)},
            {"applicationFound", application != nullptr},
        };
        if (!m_currentRequestId.isEmpty()) {
            manifest.insert("currentRequestId", m_currentRequestId);
        }
        if (!m_lastRequestId.isEmpty()) {
            manifest.insert("lastRequestId", m_lastRequestId);
        }
        if (!m_protocolError.isEmpty()) {
            manifest.insert("protocolError", m_protocolError);
        }
        if (!m_shutdownReason.isEmpty()) {
            manifest.insert("shutdownReason", m_shutdownReason);
        }

        QSaveFile file(QDir(m_sessionDirectory).filePath("session.json"));
        if (!file.open(QIODevice::WriteOnly)) {
            return;
        }
        file.write(QJsonDocument(manifest).toJson(QJsonDocument::Compact));
        file.commit();
    }

    QString m_sessionDirectory;
    QString m_runRoot;
    QString m_sessionId;
    QString m_reportId;
    QString m_state;
    QString m_currentRequestId;
    QString m_lastRequestId;
    QString m_protocolError;
    QString m_shutdownReason;
    QDateTime m_createdUtc;
    QTimer m_timer;
    QPointer<SolidProbeAgent> m_agent;
    QSet<QWidget*> m_requestBaselineTopLevels;
    qint64 m_lastActivityMs = 0;
    int m_idleTimeoutMs = 300000;
    bool m_valid = false;
    bool m_closeRequestWindows = false;
};

class SolidProbePlugin final : public QGenericPlugin
{
    Q_OBJECT
    Q_PLUGIN_METADATA(IID QGenericPluginFactoryInterface_iid FILE "solidprobe.json")

public:
    QObject* create(const QString& key, const QString&) override
    {
        if (key.compare("SolidProbe", Qt::CaseInsensitive) != 0) {
            return nullptr;
        }
        if (!qEnvironmentVariable(
                "NSIGHT_SOLID_PROBE_SESSION_DIRECTORY").trimmed().isEmpty()) {
            return new SolidProbeSessionAgent();
        }
        return new SolidProbeAgent();
    }
};

} // namespace

#include "solid_probe_plugin.moc"
