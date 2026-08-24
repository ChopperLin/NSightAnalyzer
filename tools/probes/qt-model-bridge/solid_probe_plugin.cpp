#include <QAbstractItemView>
#include <QAbstractProxyModel>
#include <QApplication>
#include <QCoreApplication>
#include <QGenericPlugin>
#include <QItemSelectionModel>
#include <QJsonArray>
#include <QJsonDocument>
#include <QJsonObject>
#include <QPointer>
#include <QSaveFile>
#include <QSet>
#include <QStackedWidget>
#include <QThread>
#include <QTimer>
#include <QTreeView>
#include <QtPlugin>

#include <algorithm>

namespace {

constexpr auto kPluginVersion = "probe-0.5";
constexpr auto kVerifiedNsightVersion = "2026.2.0";
constexpr auto kVerifiedNsightBuild = "37991608";

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
        };
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

        const QString identity = className + "\n" + objectName;
        const int instanceOrdinal = instanceOrdinals.value(identity, 0);
        instanceOrdinals.insert(identity, instanceOrdinal + 1);
        QJsonArray dynamicProperties;
        for (const QByteArray& propertyName : model->dynamicPropertyNames()) {
            dynamicProperties.append(QString::fromLatin1(propertyName));
        }

        const QJsonArray modelAttachments = attachments.value(model);
        QJsonObject entry{
            {"class", className},
            {"objectName", objectName},
            {"instanceOrdinal", instanceOrdinal},
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

class SolidProbeAgent final : public QObject
{
    Q_OBJECT

public:
    explicit SolidProbeAgent(QObject* parent = nullptr)
        : QObject(parent)
        , m_outputPath(qEnvironmentVariable("NSIGHT_SOLID_PROBE_OUTPUT"))
        , m_mode(qEnvironmentVariable("NSIGHT_SOLID_PROBE_MODE", "heartbeat"))
    {
        m_timer.setInterval(500);
        connect(&m_timer, &QTimer::timeout, this, &SolidProbeAgent::Poll);
        QTimer::singleShot(0, this, [this] {
            Poll();
            m_timer.start();
        });
    }

private slots:
    void Poll()
    {
        ++m_pollCount;

        auto* application = qobject_cast<QApplication*>(QCoreApplication::instance());
        QJsonObject root{
            {"schema", "NsightSolidProbeHeartbeatV1"},
            {"status", "loaded"},
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
        }

        if (!IsKnownMode(m_mode)) {
            root.insert("status", "error");
            root.insert("stage", "unsupported-mode");
            root.insert("error", "unsupported probe mode");
            Write(root);
            m_timer.stop();
            if (qEnvironmentVariableIntValue("NSIGHT_SOLID_PROBE_QUIT_WHEN_READY") == 1) {
                QTimer::singleShot(0, QCoreApplication::instance(), &QCoreApplication::quit);
            }
            return;
        }

        if (application != nullptr && m_mode == "selection-metrics-export") {
            const bool complete = HandleSelectionMetrics(application, root);
            if (!complete && m_pollCount >= 120) {
                root.insert("status", "timeout");
                root.insert("stage", "timeout");
            }
            Write(root);
            if (complete || m_pollCount >= 120) {
                m_timer.stop();
                if (qEnvironmentVariableIntValue("NSIGHT_SOLID_PROBE_QUIT_WHEN_READY") == 1) {
                    QTimer::singleShot(0, QCoreApplication::instance(), &QCoreApplication::quit);
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

        if ((m_mode == "heartbeat" && m_pollCount >= 10)
            || (m_mode == "event-discovery" && eventListReady)
            || (m_mode == "event-export" && eventExportReady)
            || ((m_mode == "metrics-discovery" || m_mode == "metrics-export") && metricsReady)
            || ((m_mode == "model-catalog" || m_mode == "model-export") && modelModeReady)
            || m_pollCount >= 120) {
            m_timer.stop();
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
            const QJsonArray baseline = CollectMetricViews(
                application, true, &baselineReady, &baselineTotal);
            root.insert("metricViewTotal", baselineTotal);
            root.insert("metricViewReturned", baseline.size());
            if (!baselineReady) {
                root.insert("stage", "waiting-baseline-metrics");
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
            || !qEnvironmentVariable("NSIGHT_SOLID_PROBE_ACTIVATE_PANEL").trimmed().isEmpty()) {
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

                m_beforeModelSelection = IndexSummary(
                    selectionModel, modelView->currentIndex());
                m_targetModelSelection = IndexSummary(selectionModel, target);
                modelView->selectionModel()->setCurrentIndex(
                    target,
                    QItemSelectionModel::ClearAndSelect | QItemSelectionModel::Rows);
                m_modelSelectionAppliedPoll = m_pollCount;
                m_lastMetricSnapshot.clear();
                m_stableMetricSamples = 0;

                root.insert("status", "model-selection-applied");
                root.insert("stage", "waiting-model-selection-update");
                root.insert("beforeModelSelection", m_beforeModelSelection);
                root.insert("targetModelSelection", m_targetModelSelection);
                root.insert("currentModelSelection", IndexSummary(
                    selectionModel, modelView->currentIndex()));
                return false;
            }

            root.insert("beforeModelSelection", m_beforeModelSelection);
            root.insert("targetModelSelection", m_targetModelSelection);
            const int pollsSinceModelSelection = m_pollCount - m_modelSelectionAppliedPoll;
            root.insert("pollsSinceModelSelection", pollsSinceModelSelection);
            const QJsonObject currentModelSelection = IndexSummary(
                selectionModel, modelView->currentIndex());
            const bool modelSelectionMatchesTarget = SameIndexPath(
                currentModelSelection, m_targetModelSelection);
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
            if (m_panelActivatedPoll == 0) {
                QWidget* before = owner->currentWidget();
                m_beforePanelObjectName = before == nullptr ? QString() : before->objectName();
                m_beforePanelIndex = owner->currentIndex();
                owner->setCurrentWidget(panel);
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

        QJsonArray currentModels;
        int currentModelTotal = 0;
        if (HasModelExportFilters()) {
            bool missingRequiredFilter = false;
            currentModels = CollectModels(
                application,
                true,
                &currentModelTotal,
                &missingRequiredFilter);
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
    int m_panelActivatedPoll = 0;
    int m_beforePanelIndex = -1;
    int m_lastModelCount = 0;
    int m_stableModelCountSamples = 0;
    int m_stableMetricSamples = 0;
    int m_stableStandaloneMetricSamples = 0;
    int m_lastEventSearchVisited = -1;
    int m_stableEventSearchSamples = 0;
    bool m_modelBaselineCaptured = false;
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
    QString m_beforePanelObjectName;
    QByteArray m_lastMetricSnapshot;
    QByteArray m_lastStandaloneMetricSnapshot;
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
        return new SolidProbeAgent();
    }
};

} // namespace

#include "solid_probe_plugin.moc"
