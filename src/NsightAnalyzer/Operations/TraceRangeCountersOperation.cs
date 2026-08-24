using System.Globalization;
using NsightAnalyzer.Adapters.NsightViewer2026_2;
using NsightAnalyzer.Contracts;

namespace NsightAnalyzer.Operations;

internal static class TraceRangeCountersOperation
{
    public static async Task<OperationResult> ExecuteAsync(
        string tracePath,
        string? viewerPath,
        int timeoutMs,
        int? eventOrdinal,
        string? eventPath,
        IReadOnlyList<string> counterNames,
        IReadOnlyList<string> rangeNames,
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
        var requestedCounters = counterNames
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var selectedColumns = session.Dataset.Columns
            .Where(column => requestedCounters.Any(requested =>
                requested.Equals(column.Name, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        var missingCounters = requestedCounters
            .Where(requested => !selectedColumns.Any(column =>
                requested.Equals(column.Name, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (missingCounters.Length > 0)
        {
            return OperationResult.Failure(
                ErrorCategory.NotFound,
                "counter.name_not_found",
                "One or more exact counter filters do not exist in the export.",
                string.Join(';', missingCounters));
        }

        var requestedRanges = rangeNames
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var selectedRows = requestedRanges.Length == 0
            ? session.Dataset.Rows.ToArray()
            : session.Dataset.Rows
                .Where(row => requestedRanges.Any(requested =>
                    requested.Equals(row.RangeName, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
        var missingRanges = requestedRanges
            .Where(requested => !selectedRows.Any(row =>
                requested.Equals(row.RangeName, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (missingRanges.Length > 0)
        {
            return OperationResult.Failure(
                ErrorCategory.NotFound,
                "counter.range_not_found",
                "One or more exact exported range filters do not exist.",
                string.Join(';', missingRanges));
        }

        var total = checked(selectedRows.Length * selectedColumns.Length);
        var values = new List<RangeCounterValueFact>(Math.Min(limit, total));
        var selectedOrdinal = 0;
        foreach (var row in selectedRows)
        {
            foreach (var column in selectedColumns)
            {
                if (selectedOrdinal >= cursor && values.Count < limit)
                {
                    var raw = row.Cells[column.ColumnIndex];
                    var hasNumeric = double.TryParse(
                        raw,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out var numeric) && double.IsFinite(numeric);
                    values.Add(new(
                        checked(row.RowIndex * session.Dataset.Columns.Count +
                            column.ColumnIndex - 1),
                        row.RowIndex,
                        row.RangeName,
                        row.RangeOccurrence,
                        column.ColumnIndex,
                        column.Name,
                        column.NameOccurrence,
                        string.IsNullOrEmpty(raw) ? null : raw,
                        hasNumeric ? numeric : null,
                        string.IsNullOrEmpty(raw)
                            ? "absent"
                            : hasNumeric ? "number" : "string",
                        string.IsNullOrEmpty(raw) ? "absent" : "available"));
                }
                selectedOrdinal++;
            }
        }
        int? nextCursor = cursor + values.Count < total
            ? cursor + values.Count
            : null;
        var value = new RangeCountersValue(
            session.Dataset.SeedScope,
            session.Dataset.Export,
            selectedRows.Length,
            selectedColumns.Length,
            new(
                values,
                cursor,
                limit,
                total,
                values.Count,
                nextCursor is not null,
                nextCursor));
        return OperationResult.Success(
            value,
            [
                ViewerProbeRunner.CreateProvenance(
                    session.Run,
                    artifact,
                    "trace.range-counters/v1",
                    "nsightViewerBuiltInCounterExport"),
            ],
            TraceCounterCatalogOperation.CounterWarnings(session));
    }
}
