using NsightAnalyzer.Adapters.NsightViewer2026_2;
using NsightAnalyzer.Contracts;

namespace NsightAnalyzer.Operations;

internal sealed record RangeBundle(
    RangeMetricsValue Metrics,
    RangeShadersValue Shaders,
    RangeInstructionMixValue InstructionMix,
    FactProvenance Provenance);

/// <summary>
/// Reads range metrics, the shader inventory and the range instruction mix from
/// one Viewer request.
///
/// Each of those atoms establishes the same event selection and waits for the
/// same models to settle, so calling them separately repeats the expensive part
/// three times. This is a transport optimization only: it produces the same
/// facts through the same projections, and every family still verifies the
/// selected scope for itself.
/// </summary>
internal static class RangeBundleReader
{
    public static async Task<OperationResult> ReadAsync(
        string tracePath,
        string? viewerPath,
        int timeoutMs,
        int eventOrdinal,
        IReadOnlyList<string> metricTables)
    {
        var opened = await TraceArtifactReader.OpenAsync(
            tracePath, "localWeak", timeoutMs);
        if (!opened.IsSuccess)
        {
            return OperationResult.Failure(opened.Error!);
        }
        var artifact = opened.Artifact!;

        var settings = TraceEventParametersOperation.ScopedSettings(eventOrdinal, null);
        // MODEL_CLASS_MATCH is a semicolon-separated OR list, so one request can
        // carry both profiler models. Object/attached-view/column filters are
        // omitted deliberately: they describe the shader model alone and would
        // exclude the instruction mix. Without an explicit column list the
        // bridge uses each model's own visible-view projection, which is what
        // the single-model atoms request explicitly.
        settings["MODEL_CLASS_MATCH"] =
            "NV::ShaderProfiler::UI::SampleItemTreeModel;" +
            "NV::ShaderProfiler::UI::InstructionMixModel";
        settings["MODEL_MATCH_MODE"] = "exact";
        settings["MODEL_LIMIT"] = "25000";
        settings["MODEL_SETTLE_MIN_POLLS"] = "16";
        settings["MODEL_REQUIRED_MIN_COUNT"] = "2";
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
            var root = run.Document.RootElement;
            var bundle = new RangeBundle(
                BridgeProjection.ProjectRangeMetrics(
                    root, eventOrdinal, null, metricTables, 0, int.MaxValue),
                BridgeProjection.ProjectRangeShaders(
                    root, eventOrdinal, null, null, null, 0, int.MaxValue),
                BridgeProjection.ProjectRangeInstructionMix(
                    root, eventOrdinal, null, 0, int.MaxValue),
                ViewerProbeRunner.CreateProvenance(run, artifact, "range-bundle/v1"));
            return OperationResult.Success(
                bundle, [bundle.Provenance], OperationSupport.ViewerWarnings);
        }
        catch (BridgeScopeUnsupportedException exception)
        {
            return OperationSupport.ScopeUnsupportedFailure(exception);
        }
        catch (BridgeFactNotFoundException exception)
        {
            return OperationResult.Failure(
                ErrorCategory.NotFound, exception.Code, exception.Message);
        }
        catch (BridgeSchemaException exception)
        {
            return OperationSupport.ProjectionFailure(exception);
        }
    }
}
