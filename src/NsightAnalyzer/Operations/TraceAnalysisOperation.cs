using NsightAnalyzer.Adapters.NsightViewer2026_2;
using NsightAnalyzer.Contracts;

namespace NsightAnalyzer.Operations;

internal static class TraceAnalysisOperation
{
    public static async Task<OperationResult> ExecuteAsync(
        string tracePath,
        string? viewerPath,
        int timeoutMs,
        int? eventOrdinal,
        string? eventPath,
        int cursor,
        int limit)
    {
        var opened = await TraceArtifactReader.OpenAsync(
            tracePath, "localWeak", timeoutMs);
        if (!opened.IsSuccess)
        {
            return OperationResult.Failure(opened.Error!);
        }
        var artifact = opened.Artifact!;
        var settings = TraceEventParametersOperation.ScopedSettings(
            eventOrdinal, eventPath);
        settings["METRIC_SETTLE_MAX_POLLS"] = "400";
        settings["MODEL_CLASS_MATCH"] =
            "NV::WarpViz::MarkerTreeModel;NV::WarpViz::AnnotationsTreeModel";
        settings["MODEL_MATCH_MODE"] = "exact";
        settings["MODEL_COLUMNS"] = "0,1,2,3,4,5,6";
        settings["MODEL_FLAT"] = "1";
        settings["MODEL_LIMIT"] = "5000";
        settings["MODEL_SETTLE_MIN_POLLS"] = "16";
        settings["MODEL_REQUIRED_MIN_COUNT"] = "1";
        settings["EXTERNAL_KILL_ON_TIMEOUT"] = "1";
        settings["ACTIVATE_PANEL"] = "FlatTabPanel_Timeline";
        settings["ACTIVATE_PANEL_VIA_BUTTON"] = "1";
        settings["PANEL_SETTLE_MIN_POLLS"] = "6";
        settings["ACTION_TRIGGER_TEXT_MATCH"] = "Trace Analysis...";
        settings["ACTION_MATCH_MODE"] = "exact";
        settings["ACTION_TRIGGER_OCCURRENCE"] = "0";
        settings["ACTION_TRIGGER_ASYNC"] = "1";
        settings["ACTION_SETTLE_MIN_POLLS"] = "16";

        var probe = await ViewerProbeRunner.RunAsync(
            artifact,
            ViewerProbeMode.SelectionMetricsExport,
            "NsightSolidProbeSelectionMetricsV1",
            settings,
            viewerPath,
            timeoutMs);
        if (!probe.IsSuccess)
        {
            return OperationResult.Failure(probe.Error!);
        }

        using var run = probe.Run!;
        try
        {
            var value = BridgeProjection.ProjectTraceAnalysis(
                run.Document.RootElement,
                eventOrdinal,
                eventPath,
                cursor,
                limit);
            return OperationResult.Success(
                value,
                [
                    ViewerProbeRunner.CreateProvenance(
                        run, artifact, "trace.analysis/v1"),
                ],
                OperationSupport.ViewerWarnings);
        }
        catch (BridgeFactNotFoundException exception)
        {
            return OperationResult.Failure(
                ErrorCategory.NotFound,
                exception.Code,
                exception.Message);
        }
        catch (BridgeSchemaException exception)
        {
            return OperationSupport.ProjectionFailure(exception);
        }
    }
}
