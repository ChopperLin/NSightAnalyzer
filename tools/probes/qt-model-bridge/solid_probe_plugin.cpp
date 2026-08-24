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
#include <QThread>
#include <QTimer>
#include <QtPlugin>

namespace {

constexpr auto kPluginVersion = "probe-0.3";

QJsonValue VariantToJson(const QVariant& value)
{
    if (!value.isValid() || value.isNull()) {
        return QJsonValue();
    }

    const QJsonValue jsonValue = QJsonValue::fromVariant(value);
    return jsonValue.isUndefined() ? QJsonValue(value.toString()) : jsonValue;
}

struct ExportState
{
    QJsonArray nodes;
    int nodeCount = 0;
    int maxNodes = 25000;
    int maxDepth = 24;
    bool truncated = false;
};

void AppendRows(
    QAbstractItemModel* model,
    const QModelIndex& parent,
    int depth,
    const QJsonArray& parentPath,
    ExportState& state)
{
    if (model == nullptr || depth >= state.maxDepth) {
        if (model != nullptr && model->rowCount(parent) > 0) {
            state.truncated = true;
        }
        return;
    }

    const int rows = model->rowCount(parent);
    const int columns = model->columnCount(parent);
    for (int row = 0; row < rows; ++row) {
        if (state.nodeCount >= state.maxNodes) {
            state.truncated = true;
            return;
        }

        const QModelIndex treeIndex = model->index(row, 0, parent);
        if (!treeIndex.isValid()) {
            continue;
        }

        QJsonArray path = parentPath;
        path.append(row);

        QJsonArray cells;
        for (int column = 0; column < columns; ++column) {
            const QModelIndex cellIndex = model->index(row, column, parent);
            const QVariant display = model->data(cellIndex, Qt::DisplayRole);
            const QVariant tooltip = model->data(cellIndex, Qt::ToolTipRole);

            QJsonObject cell{
                {"column", column},
                {"display", VariantToJson(display)},
            };
            if (tooltip.isValid() && tooltip != display) {
                cell.insert("tooltip", VariantToJson(tooltip));
            }
            cells.append(cell);
        }

        const int childCount = model->rowCount(treeIndex);
        state.nodes.append(QJsonObject{
            {"path", path},
            {"depth", depth},
            {"row", row},
            {"childCount", childCount},
            {"cells", cells},
        });
        ++state.nodeCount;

        if (childCount > 0) {
            AppendRows(model, treeIndex, depth + 1, path, state);
        }
    }
}

QJsonObject ExportModel(QAbstractItemModel* model)
{
    ExportState state;
    const int configuredMaxNodes = qEnvironmentVariableIntValue("NSIGHT_SOLID_PROBE_MAX_NODES");
    const int configuredMaxDepth = qEnvironmentVariableIntValue("NSIGHT_SOLID_PROBE_MAX_DEPTH");
    if (configuredMaxNodes > 0) {
        state.maxNodes = configuredMaxNodes;
    }
    if (configuredMaxDepth > 0) {
        state.maxDepth = configuredMaxDepth;
    }

    QJsonArray headers;
    const int columns = model->columnCount();
    for (int column = 0; column < columns; ++column) {
        headers.append(QJsonObject{
            {"column", column},
            {"display", VariantToJson(model->headerData(column, Qt::Horizontal, Qt::DisplayRole))},
        });
    }

    AppendRows(model, QModelIndex(), 0, QJsonArray(), state);
    return QJsonObject{
        {"modelClass", model->metaObject()->className()},
        {"modelObjectName", model->objectName()},
        {"rootRows", model->rowCount()},
        {"rootColumns", columns},
        {"headers", headers},
        {"nodes", state.nodes},
        {"nodeCount", state.nodeCount},
        {"maxNodes", state.maxNodes},
        {"maxDepth", state.maxDepth},
        {"truncated", state.truncated},
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

QJsonArray CollectMetricViews(QApplication* application, bool includeData, bool* ready)
{
    QJsonArray matches;
    *ready = false;
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

        QAbstractItemModel* model = view->model();
        QJsonArray modelChain;
        QAbstractItemModel* sourceModel = UnwrapProxyModel(model, &modelChain);
        const int rows = model->rowCount();
        const int columns = model->columnCount();
        QJsonObject match{
            {"viewClass", viewClass},
            {"viewObjectName", widget->objectName()},
            {"visible", widget->isVisible()},
            {"modelClass", model->metaObject()->className()},
            {"modelObjectName", model->objectName()},
            {"modelChain", modelChain},
            {"sourceRows", sourceModel->rowCount()},
            {"sourceColumns", sourceModel->columnCount()},
            {"rows", rows},
            {"columns", columns},
        };
        if (includeData) {
            match.insert("export", ExportModel(model));
        }
        matches.append(match);
        *ready = *ready || (sourceModel->rowCount() > 0 && sourceModel->columnCount() > 0);
    }
    return matches;
}

QModelIndex FindIndexByDescription(
    QAbstractItemModel* model,
    const QString& needle,
    bool exact,
    const QModelIndex& parent,
    int depth,
    int& visited)
{
    if (model == nullptr || needle.isEmpty() || depth >= 24 || visited >= 25000) {
        return QModelIndex();
    }

    const int rows = model->rowCount(parent);
    for (int row = 0; row < rows; ++row) {
        if (++visited > 25000) {
            return QModelIndex();
        }

        const QModelIndex index = model->index(row, 0, parent);
        if (!index.isValid()) {
            continue;
        }
        const QString description = model->data(index, Qt::DisplayRole).toString();
        const bool matches = exact
            ? description.compare(needle, Qt::CaseInsensitive) == 0
            : description.contains(needle, Qt::CaseInsensitive);
        if (matches) {
            return index;
        }

        const QModelIndex childMatch = FindIndexByDescription(
            model, needle, exact, index, depth + 1, visited);
        if (childMatch.isValid()) {
            return childMatch;
        }
    }
    return QModelIndex();
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

class SolidProbeAgent final : public QObject
{
    Q_OBJECT

public:
    explicit SolidProbeAgent(QObject* parent = nullptr)
        : QObject(parent)
        , m_outputPath(qEnvironmentVariable("NSIGHT_SOLID_PROBE_OUTPUT"))
        , m_mode(qEnvironmentVariable("NSIGHT_SOLID_PROBE_MODE", "heartbeat"))
        , m_eventMatch(qEnvironmentVariable(
              "NSIGHT_SOLID_PROBE_EVENT_MATCH", "DescriptorHeapSample::onRender"))
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
        };

        bool eventListReady = false;
        bool eventExportReady = false;
        bool metricsReady = false;
        if (application != nullptr) {
            root.insert("applicationClass", application->metaObject()->className());
            root.insert("topLevelWindowCount", application->topLevelWidgets().size());
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
                        eventListReady = rows > 0 && columns > 0;

                        if (eventListReady && m_mode == "event-export") {
                            int minimumPoll = qEnvironmentVariableIntValue(
                                "NSIGHT_SOLID_PROBE_EVENT_EXPORT_MIN_POLL");
                            minimumPoll = qMax(1, minimumPoll);
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
                            match.insert("export", ExportModel(extractionModel));
                        }
                    }
                }
                matches.append(match);
            }
            root.insert("eventViews", matches);
            root.insert("eventListReady", eventListReady);
            if (m_mode == "event-export") {
                root.insert("schema", "NsightSolidProbeEventListV1");
            }
        }

        if (application != nullptr
            && (m_mode == "metrics-discovery" || m_mode == "metrics-export")) {
            const QJsonArray matches = CollectMetricViews(
                application, m_mode == "metrics-export", &metricsReady);
            root.insert(
                "schema",
                m_mode == "metrics-export"
                    ? "NsightSolidProbeMetricsV1"
                    : "NsightSolidProbeMetricsDiscoveryV1");
            root.insert("metricViews", matches);
            root.insert("metricsReady", metricsReady);
        }

        Write(root);

        if ((m_mode == "heartbeat" && m_pollCount >= 10)
            || (m_mode == "event-discovery" && eventListReady)
            || (m_mode == "event-export" && eventExportReady)
            || ((m_mode == "metrics-discovery" || m_mode == "metrics-export") && metricsReady)
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
            if (quitAfterHeartbeat || quitWhenReady || quitWhenMetricsReady) {
                QTimer::singleShot(0, QCoreApplication::instance(), &QCoreApplication::quit);
            }
        }
    }

private:
    bool HandleSelectionMetrics(QApplication* application, QJsonObject& root)
    {
        root.insert("schema", "NsightSolidProbeSelectionMetricsV1");
        root.insert("eventMatch", m_eventMatch);

        QAbstractItemView* eventView = nullptr;
        const auto widgets = application->allWidgets();
        for (QWidget* widget : widgets) {
            if (widget != nullptr && widget->objectName() == "EventList_EventTreeView") {
                eventView = qobject_cast<QAbstractItemView*>(widget);
                if (eventView != nullptr) {
                    break;
                }
            }
        }

        if (eventView == nullptr || eventView->model() == nullptr) {
            root.insert("stage", "waiting-event-list");
            return false;
        }

        QAbstractItemModel* eventModel = eventView->model();
        root.insert("eventModelClass", eventModel->metaObject()->className());
        root.insert("currentSelection", IndexSummary(eventModel, eventView->currentIndex()));

        if (m_selectionAppliedPoll == 0) {
            int visited = 0;
            QModelIndex target = FindIndexByDescription(
                eventModel, m_eventMatch, true, QModelIndex(), 0, visited);
            QString matchKind = "exact";
            if (!target.isValid()) {
                visited = 0;
                target = FindIndexByDescription(
                    eventModel, m_eventMatch, false, QModelIndex(), 0, visited);
                matchKind = "contains";
            }
            root.insert("eventNodesVisited", visited);
            root.insert("eventMatchKind", matchKind);
            if (!target.isValid()) {
                root.insert("stage", "waiting-target-event");
                return false;
            }

            bool baselineReady = false;
            const QJsonArray baseline = CollectMetricViews(application, true, &baselineReady);
            if (!baselineReady) {
                root.insert("stage", "waiting-baseline-metrics");
                return false;
            }
            if (eventView->selectionModel() == nullptr) {
                root.insert("status", "error");
                root.insert("stage", "missing-selection-model");
                return false;
            }

            m_baselineMetricViews = baseline;
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
        root.insert("pollsSinceSelection", m_pollCount - m_selectionAppliedPoll);
        if (m_pollCount - m_selectionAppliedPoll < 6) {
            root.insert("status", "selection-applied");
            root.insert("stage", "waiting-metrics-update");
            return false;
        }

        bool metricsReady = false;
        const QJsonArray selectedMetricViews = CollectMetricViews(application, true, &metricsReady);
        if (!metricsReady) {
            root.insert("stage", "waiting-selected-metrics");
            return false;
        }

        root.insert("baselineMetricViews", m_baselineMetricViews);
        root.insert("metricViews", selectedMetricViews);
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
    QString m_eventMatch;
    QTimer m_timer;
    int m_pollCount = 0;
    int m_selectionAppliedPoll = 0;
    QJsonArray m_baselineMetricViews;
    QJsonObject m_beforeSelection;
    QJsonObject m_targetSelection;
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
