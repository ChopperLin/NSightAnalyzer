using NsightAnalyzer.Adapters.NsightViewer2026_2;
using NsightAnalyzer.Contracts;

namespace NsightAnalyzer.Operations;

internal static class TraceRangeMetricCatalogOperation
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
        settings["POLL_INTERVAL_MS"] = "100";
        settings["METRIC_CATALOG_ONLY"] = "1";
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
            var value = BridgeProjection.ProjectRangeMetricCatalog(
                run.Document.RootElement,
                eventOrdinal,
                eventPath,
                cursor,
                limit);
            return OperationResult.Success(
                value,
                [ViewerProbeRunner.CreateProvenance(
                    run, artifact, "trace.range-metric-catalog/v1")],
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
