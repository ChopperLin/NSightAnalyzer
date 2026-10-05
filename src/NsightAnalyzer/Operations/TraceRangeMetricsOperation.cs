using NsightAnalyzer.Adapters.NsightViewer2026_2;
using NsightAnalyzer.Contracts;

namespace NsightAnalyzer.Operations;

internal static class TraceRangeMetricsOperation
{
    public static async Task<OperationResult> ExecuteAsync(
        string tracePath,
        string? viewerPath,
        int timeoutMs,
        int? eventOrdinal,
        string? eventPath,
        IReadOnlyList<string> metricTables,
        int cursor,
        int limit)
    {
        var opened = await TraceArtifactReader.OpenAsync(
            tracePath, "localWeak", timeoutMs);
        if (!opened.IsSuccess)
        {
            return OperationResult.Failure(opened.Error!);
        }
        return await RangeMetricSnapshotReader.ReadAsync(
            opened.Artifact!, viewerPath, timeoutMs, eventOrdinal, eventPath,
            metricTables, cursor, limit);
    }
}
