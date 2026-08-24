using NsightAnalyzer.Adapters.NsightViewer2026_2;
using NsightAnalyzer.Contracts;

namespace NsightAnalyzer.Operations;

internal static class TraceRangeShadersOperation
{
    public static async Task<OperationResult> ExecuteAsync(
        string tracePath,
        string? viewerPath,
        int timeoutMs,
        int? eventOrdinal,
        string? eventPath,
        string? shaderHash,
        int? shaderOccurrence,
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
        settings["MODEL_CLASS_MATCH"] =
            "NV::ShaderProfiler::UI::SampleItemTreeModel";
        settings["MODEL_OBJECT_MATCH"] = "SampleItemModel";
        settings["MODEL_MATCH_MODE"] = "exact";
        settings["MODEL_ATTACHED_VIEW_OBJECT_MATCH"] = "SampleTreeView";
        settings["MODEL_COLUMNS"] = "0,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,38";
        settings["MODEL_LIMIT"] = "25000";
        settings["MODEL_SETTLE_MIN_POLLS"] = "16";
        settings["ACTIVATE_PANEL"] = "FlatTabPanel_Shader Pipelines";
        settings["ACTIVATE_PANEL_VIA_BUTTON"] = "1";
        settings["PANEL_SETTLE_MIN_POLLS"] = "6";
        settings["COMBO_OBJECT_MATCH"] = "GroupByComboBox";
        settings["COMBO_MATCH_MODE"] = "exact";
        settings["COMBO_SELECT_MATCH"] = "Pipeline Object";
        settings["COMBO_SELECT_MATCH_MODE"] = "exact";
        settings["COMBO_SELECT_TRIGGER"] = "activated";
        settings["COMBO_SELECT_SETTLE_MIN_POLLS"] = "12";

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
            var value = BridgeProjection.ProjectRangeShaders(
                run.Document.RootElement,
                eventOrdinal,
                eventPath,
                shaderHash,
                shaderOccurrence,
                cursor,
                limit);
            return OperationResult.Success(
                value,
                [
                    ViewerProbeRunner.CreateProvenance(
                        run, artifact, "trace.range-shaders/v1"),
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
