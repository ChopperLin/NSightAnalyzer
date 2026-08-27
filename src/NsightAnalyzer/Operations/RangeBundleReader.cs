using NsightAnalyzer.Adapters.NsightViewer2026_2;
using NsightAnalyzer.Contracts;

namespace NsightAnalyzer.Operations;

internal sealed record RangeBundle(
    RangeMetricsValue Metrics,
    RangeShadersValue Shaders,
    RangeInstructionMixValue? InstructionMix,
    OperationError? InstructionMixError,
    FactProvenance Provenance);

/// <summary>
/// Reads range metrics and the shader inventory from one Viewer request, with
/// range Instruction Mix included only for the strict comparison path.
///
/// Each of those atoms establishes the same event selection and waits for the
/// same models to settle, so calling them separately repeats the expensive part
/// three times. This is a transport optimization only: it produces the same
/// facts through the same projections, and every available family still
/// verifies the selected scope for itself. inspect-pass/v1 deliberately leaves
/// the stateful Instruction Mix model to its explicit atom, so its stable
/// metrics/shader snapshot can share the bounded range-shader cache. The strict
/// compare-ranges path still requests Instruction Mix and remains uncached.
/// </summary>
internal static class RangeBundleReader
{
    public static async Task<OperationResult> ReadAsync(
        string tracePath,
        string? viewerPath,
        int timeoutMs,
        int eventOrdinal,
        IReadOnlyList<string> metricTables,
        bool includeInstructionMix)
    {
        var opened = await TraceArtifactReader.OpenAsync(
            tracePath, "localWeak", timeoutMs);
        if (!opened.IsSuccess)
        {
            return OperationResult.Failure(opened.Error!);
        }
        var artifact = opened.Artifact!;

        Dictionary<string, string> settings;
        ViewerProbeCachePolicy cachePolicy;
        if (includeInstructionMix)
        {
            settings = TraceEventParametersOperation.ScopedSettings(eventOrdinal, null);
            // MODEL_CLASS_MATCH is a semicolon-separated OR list, so one request can
            // carry both profiler models. Shader-only filters are omitted because
            // they would exclude the Instruction Mix model.
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
            settings["POLL_INTERVAL_MS"] = "200";
            cachePolicy = ViewerProbeCachePolicy.None;
        }
        else
        {
            settings = TraceRangeShadersOperation.BridgeSettings(eventOrdinal, null);
            cachePolicy = ViewerProbeCachePolicy.RangeShadersV1;
        }

        var probe = await ViewerProbeRunner.RunAsync(
            artifact,
            ViewerProbeMode.SelectionMetricsExport,
            "NsightSolidProbeSelectionMetricsV1",
            settings,
            viewerPath,
            timeoutMs,
            cachePolicy);
        if (!probe.IsSuccess)
        {
            return OperationResult.Failure(probe.Error!);
        }

        using var run = probe.Run!;
        try
        {
            var root = run.Document.RootElement;
            RangeInstructionMixValue? instructionMix = null;
            OperationError? instructionMixError;
            if (includeInstructionMix)
            {
                instructionMixError = null;
                try
                {
                    instructionMix = BridgeProjection.ProjectRangeInstructionMix(
                        root, eventOrdinal, null, 0, int.MaxValue);
                }
                catch (BridgeFactUnavailableException exception)
                {
                    instructionMixError = new(
                        ErrorCategory.Unavailable,
                        exception.Code,
                        exception.Message,
                        exception.Detail);
                }
            }
            else
            {
                instructionMixError = new(
                    ErrorCategory.Unavailable,
                    "trace.range_instruction_mix_not_requested",
                    "inspect-pass/v1 does not request the stateful Instruction Mix model; call trace.range-instruction-mix for that fact family.");
            }

            var bundle = new RangeBundle(
                BridgeProjection.ProjectRangeMetrics(
                    root, eventOrdinal, null, metricTables, 0, int.MaxValue),
                BridgeProjection.ProjectRangeShaders(
                    root, eventOrdinal, null, null, null, 0, int.MaxValue),
                instructionMix,
                instructionMixError,
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
        catch (BridgeFactUnavailableException exception)
        {
            return OperationResult.Failure(
                ErrorCategory.Unavailable,
                exception.Code,
                exception.Message,
                exception.Detail);
        }
        catch (BridgeSchemaException exception)
        {
            return OperationSupport.ProjectionFailure(exception);
        }
    }
}
