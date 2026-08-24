using System.Globalization;
using NsightAnalyzer.Adapters.NsightViewer2026_2;
using NsightAnalyzer.Contracts;

namespace NsightAnalyzer.Operations;

internal static class TraceShaderSourceOperation
{
    public static async Task<OperationResult> ExecuteAsync(
        string tracePath,
        string? viewerPath,
        int timeoutMs,
        int? eventOrdinal,
        string? eventPath,
        string shaderHash,
        int shaderOccurrence,
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
            "NV::SourceCorrelation::SourceModel";
        settings["MODEL_MATCH_MODE"] = "exact";
        settings["MODEL_COLUMNS"] = "0,2,3,4,5,6,7,25,26,27,28,29,30";
        settings["MODEL_LIMIT"] = "25000";
        settings["MODEL_SETTLE_MIN_POLLS"] = "12";
        settings["MODEL_SELECT_VIEW_OBJECT"] = "SampleTreeView";
        settings["MODEL_SELECT_MATCH"] = shaderHash;
        settings["MODEL_SELECT_COLUMN"] = "3";
        settings["MODEL_SELECT_MATCH_MODE"] = "exact";
        settings["MODEL_SELECT_OCCURRENCE"] =
            shaderOccurrence.ToString(CultureInfo.InvariantCulture);
        settings["MODEL_SELECT_PREPARE_PANEL"] =
            "FlatTabPanel_Shader Pipelines";
        settings["MODEL_SELECT_PREPARE_PANEL_SETTLE_MIN_POLLS"] = "4";
        settings["MODEL_SELECT_SETTLE_MIN_POLLS"] = "6";
        settings["MODEL_SELECT_PROVIDER_STABLE_MIN_POLLS"] = "2";
        settings["MODEL_SELECT_DEFER_PROVIDER_VERIFY_UNTIL_COMBO"] = "1";
        settings["ACTIVATE_PANEL"] = "FlatTabPanel_Shader Source";
        settings["ACTIVATE_PANEL_VIA_BUTTON"] = "1";
        settings["PANEL_SETTLE_MIN_POLLS"] = "6";
        settings["COMBO_ANCESTRY_CLASS_MATCH"] =
            "NV::ShaderProfiler::UI::SourceDetails";
        settings["COMBO_MATCH_MODE"] = "exact";
        settings["COMBO_SELECT_FROM_MODEL_SELECTION"] = "shader-source";
        settings["COMBO_SELECT_MATCH_MODE"] = "exact";
        settings["COMBO_SELECT_OCCURRENCE"] = "0";
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
            var value = BridgeProjection.ProjectShaderSource(
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
                        run, artifact, "trace.shader-source/v1"),
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
