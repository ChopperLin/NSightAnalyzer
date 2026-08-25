using NsightAnalyzer.Adapters.NsightViewer2026_2;
using NsightAnalyzer.Contracts;

namespace NsightAnalyzer.Operations;

internal static class TraceRangeInstructionMixOperation
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
        settings["MODEL_CLASS_MATCH"] =
            "NV::ShaderProfiler::UI::InstructionMixModel";
        settings["MODEL_MATCH_MODE"] = "exact";
        settings["MODEL_COLUMNS"] = "0,1,2,3,4";
        settings["MODEL_LIMIT"] = "1000";
        settings["MODEL_SETTLE_MIN_POLLS"] = "16";
        settings["MODEL_REQUIRED_MIN_COUNT"] = "1";
        settings["ACTIVATE_PANEL"] = "FlatTabPanel_Instruction Mix";
        settings["ACTIVATE_PANEL_VIA_BUTTON"] = "1";
        settings["PANEL_SETTLE_MIN_POLLS"] = "6";

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
            var value = BridgeProjection.ProjectRangeInstructionMix(
                run.Document.RootElement,
                eventOrdinal,
                eventPath,
                cursor,
                limit);
            return OperationResult.Success(
                value,
                [
                    ViewerProbeRunner.CreateProvenance(
                        run, artifact, "trace.range-instruction-mix/v1"),
                ],
                OperationSupport.ViewerWarnings);
        }
        catch (BridgeScopeUnsupportedException exception)
        {
            return OperationSupport.ScopeUnsupportedFailure(exception);
        }
        catch (BridgeSchemaException exception)
        {
            return OperationSupport.ProjectionFailure(exception);
        }
    }
}
