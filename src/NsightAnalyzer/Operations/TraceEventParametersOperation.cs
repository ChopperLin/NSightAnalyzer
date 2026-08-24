using System.Globalization;
using NsightAnalyzer.Adapters.NsightViewer2026_2;
using NsightAnalyzer.Contracts;

namespace NsightAnalyzer.Operations;

internal static class TraceEventParametersOperation
{
    public static async Task<OperationResult> ExecuteAsync(
        string tracePath,
        string? viewerPath,
        int timeoutMs,
        int? eventOrdinal,
        string? eventPath)
    {
        var opened = await TraceArtifactReader.OpenAsync(
            tracePath, "localWeak", timeoutMs);
        if (!opened.IsSuccess)
        {
            return OperationResult.Failure(opened.Error!);
        }
        var artifact = opened.Artifact!;
        var settings = ScopedSettings(eventOrdinal, eventPath);
        settings["MODEL_CLASS_MATCH"] =
            "NV::EventParameters::EventParametersTreeModel";
        settings["MODEL_MATCH_MODE"] = "exact";
        settings["MODEL_COLUMNS"] = "0,1,2";
        settings["MODEL_LIMIT"] = "1000";
        settings["MODEL_SETTLE_MIN_POLLS"] = "12";

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
            var value = BridgeProjection.ProjectEventParameters(
                run.Document.RootElement, eventOrdinal, eventPath);
            return OperationResult.Success(
                value,
                [
                    ViewerProbeRunner.CreateProvenance(
                        run, artifact, "trace.event-parameters/v1"),
                ],
                OperationSupport.ViewerWarnings);
        }
        catch (BridgeSchemaException exception)
        {
            return OperationSupport.ProjectionFailure(exception);
        }
    }

    internal static Dictionary<string, string> ScopedSettings(
        int? eventOrdinal,
        string? eventPath)
    {
        var settings = new Dictionary<string, string>
        {
            ["SELECTION_BASELINE_METRIC_MIN_COUNT"] = "80",
            ["SELECTION_BASELINE_METRIC_STABLE_MIN_POLLS"] = "2",
            ["METRIC_SETTLE_MIN_POLLS"] = "4",
            ["METRIC_SETTLE_MAX_POLLS"] = "60",
            ["METRIC_LIMIT"] = "1000",
        };
        if (eventOrdinal is not null)
        {
            settings["EVENT_ORDINAL"] =
                eventOrdinal.Value.ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            settings["EVENT_PATH"] = eventPath!.Replace('.', '/');
        }
        return settings;
    }
}
