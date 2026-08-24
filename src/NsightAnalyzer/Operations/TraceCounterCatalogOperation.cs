using NsightAnalyzer.Adapters.NsightViewer2026_2;
using NsightAnalyzer.Contracts;

namespace NsightAnalyzer.Operations;

internal static class TraceCounterCatalogOperation
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
        var exported = await CounterExportReader.ExportAsync(
            artifact, viewerPath, timeoutMs, eventOrdinal, eventPath);
        if (!exported.IsSuccess)
        {
            return OperationResult.Failure(exported.Error!);
        }

        using var session = exported.Session!;
        var columns = session.Dataset.Columns;
        CounterColumnFact[] page = cursor >= columns.Count
            ? []
            : columns.Skip(cursor).Take(limit)
                .Select(column => new CounterColumnFact(
                    column.ColumnIndex,
                    column.Name,
                    column.NameOccurrence,
                    column.Unit))
                .ToArray();
        int? nextCursor = cursor + page.Length < columns.Count
            ? cursor + page.Length
            : null;
        var value = new CounterCatalogValue(
            session.Dataset.SeedScope,
            session.Dataset.Export,
            new(
                page,
                cursor,
                limit,
                columns.Count,
                page.Length,
                nextCursor is not null,
                nextCursor));
        return OperationResult.Success(
            value,
            [
                ViewerProbeRunner.CreateProvenance(
                    session.Run,
                    artifact,
                    "trace.counter-catalog/v1",
                    "nsightViewerBuiltInCounterExport"),
            ],
            CounterWarnings(session));
    }

    internal static IReadOnlyList<OperationWarning> CounterWarnings(
        CounterExportSession session) =>
        OperationSupport.ViewerWarnings
            .Concat(session.Warnings)
            .Append(new(
                "counter.export_artifacts_retained",
                "Validated counter evidence files remain in the isolated local run directory."))
            .ToArray();
}
