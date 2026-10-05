using NsightAnalyzer.Adapters.NsightViewer2026_2;
using NsightAnalyzer.Contracts;

namespace NsightAnalyzer.Operations;

internal sealed record RangeBundle(
    RangeMetricsValue? Metrics,
    RangeShadersValue? Shaders,
    FactProvenance Provenance);

// Share the proven metric/shader snapshot only when those families are requested.
// Instruction Mix remains an independent, uncached atom.
internal static class RangeBundleReader
{
    public static async Task<OperationResult> ReadAsync(
        string tracePath,
        string? viewerPath,
        int timeoutMs,
        int eventOrdinal,
        IReadOnlyList<string> metricTables,
        RangeSections sections)
    {
        var opened = await TraceArtifactReader.OpenAsync(
            tracePath, "localWeak", timeoutMs);
        if (!opened.IsSuccess)
        {
            return OperationResult.Failure(opened.Error!);
        }
        var artifact = opened.Artifact!;

        var includeMetrics = sections.HasFlag(RangeSections.Metrics);
        var includeShaders = sections.HasFlag(RangeSections.Shaders);
        if (includeMetrics && !includeShaders)
        {
            var metricResult = await RangeMetricSnapshotReader.ReadAsync(artifact,
                viewerPath, timeoutMs, eventOrdinal, null, metricTables, 0, int.MaxValue);
            if (!metricResult.IsSuccess) return metricResult;
            var provenance = metricResult.Provenance![0];
            return OperationResult.Success(new RangeBundle(
                (RangeMetricsValue)metricResult.Value!, null, provenance),
                metricResult.Provenance, metricResult.Warnings);
        }
        if (!includeMetrics && !includeShaders)
        {
            throw new ArgumentException("A metric or shader section is required.", nameof(sections));
        }
        var settings = includeShaders
            ? TraceRangeShadersOperation.BridgeSettings(eventOrdinal, null)
            : TraceEventParametersOperation.ScopedSettings(eventOrdinal, null);
        var cachePolicy = includeShaders
            ? ViewerProbeCachePolicy.RangeShadersV1
            : ViewerProbeCachePolicy.None;

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
            var bundle = new RangeBundle(
                includeMetrics ? BridgeProjection.ProjectRangeMetrics(
                    root, eventOrdinal, null, metricTables, 0, int.MaxValue) : null,
                includeShaders ? BridgeProjection.ProjectRangeShaders(
                    root, eventOrdinal, null, null, null, 0, int.MaxValue) : null,
                ViewerProbeRunner.CreateProvenance(run, artifact, "range-bundle/v2"));
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
