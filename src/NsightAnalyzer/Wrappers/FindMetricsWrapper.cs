using NsightAnalyzer.Contracts;
using NsightAnalyzer.Operations;

namespace NsightAnalyzer.Wrappers;

internal static class FindMetricsWrapper
{
    public static async Task<OperationResult> ExecuteAsync(
        string tracePath,
        string? viewerPath,
        int timeoutMs,
        int eventOrdinal,
        IReadOnlyList<string> metricTables,
        string? nameContains,
        int? sourceOrdinal,
        int cursor,
        int limit)
    {
        var inputError = ValidateQuery(nameContains, sourceOrdinal, cursor, limit);
        if (inputError is not null)
        {
            return OperationResult.Failure(inputError);
        }

        var context = new WrapperContext(new WrapperDeadline(timeoutMs));
        var result = await context.InvokeAtomAsync(remainingMs =>
            RangeBundleReader.ReadAsync(tracePath, viewerPath, remainingMs,
                eventOrdinal, metricTables, RangeSections.Metrics));
        if (!result.IsSuccess)
        {
            return result;
        }
        if (result.Value is not RangeBundle { Metrics: not null } bundle ||
            bundle.Metrics.Scope.PreorderOrdinal != eventOrdinal)
        {
            return OperationResult.Failure(WrapperSupport.InternalError(
                "The complete metric read did not return the requested range scope."));
        }

        context.AddRetrievedFacts(bundle.Metrics.Metrics.ReturnedCount);
        var projected = Project(bundle.Metrics, metricTables, nameContains,
            sourceOrdinal, cursor, limit, context.Stats());
        return projected.IsSuccess
            ? OperationResult.Success(projected.Value!, context.Provenance, context.Warnings)
            : OperationResult.Failure(projected.Error!);
    }

    internal static WrapperValueResult<FindMetricsValue> Project(
        RangeMetricsValue source,
        IReadOnlyList<string> metricTables,
        string? nameContains,
        int? sourceOrdinal,
        int cursor,
        int limit,
        WrapperExecutionStats execution)
    {
        var inputError = ValidateQuery(nameContains, sourceOrdinal, cursor, limit);
        if (inputError is not null)
        {
            return WrapperValueResult<FindMetricsValue>.Failure(inputError);
        }
        var page = source.Metrics;
        var pageError = WrapperSupport.ValidatePage(page, 0, page.Limit);
        if (pageError is not null || page.Truncated || page.ReturnedCount != page.TotalCount)
        {
            return WrapperValueResult<FindMetricsValue>.Failure(pageError ??
                WrapperSupport.InternalError("Metric search requires complete source facts."));
        }
        if (page.Items.Any(item => item.SourceOrdinal < 0) ||
            page.Items.Select(item => item.SourceOrdinal).Distinct().Count() != page.Items.Count)
        {
            return WrapperValueResult<FindMetricsValue>.Failure(
                WrapperSupport.InternalError("Metric source ordinals are not unique within the range."));
        }

        var selected = page.Items.Where(item => sourceOrdinal is not null
                ? item.SourceOrdinal == sourceOrdinal.Value
                : item.TableName.Contains(nameContains!, StringComparison.OrdinalIgnoreCase) ||
                  item.RowName.Contains(nameContains!, StringComparison.OrdinalIgnoreCase) ||
                  item.ColumnName.Contains(nameContains!, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.SourceOrdinal)
            .ToArray();
        if (sourceOrdinal is not null && selected.Length == 0)
        {
            return WrapperValueResult<FindMetricsValue>.Failure(new(
                ErrorCategory.NotFound,
                "wrapper.metric_not_found",
                "The exact metric source ordinal was not found in the selected range and tables.",
                $"sourceOrdinal={sourceOrdinal.Value}"));
        }

        return WrapperValueResult<FindMetricsValue>.Success(new(
            source.Scope,
            new(nameContains, sourceOrdinal, metricTables,
                "tableName,rowName,columnName", "sourceOrdinal"),
            source.TableCount,
            source.RowCount,
            page.TotalCount,
            WrapperSupport.Page(selected, cursor, limit),
            execution));
    }

    private static OperationError? ValidateQuery(
        string? nameContains, int? sourceOrdinal, int cursor, int limit)
    {
        if ((nameContains is null) == (sourceOrdinal is null) ||
            nameContains is not null && string.IsNullOrWhiteSpace(nameContains) ||
            sourceOrdinal < 0 || cursor < 0 || limit is < 1 or > ContractLimits.MaximumPageLimit)
        {
            return new(ErrorCategory.InvalidInput, "wrapper.metric_query_invalid",
                "Provide either a nonempty metric name substring or an exact source ordinal, with valid paging.");
        }
        return null;
    }
}
