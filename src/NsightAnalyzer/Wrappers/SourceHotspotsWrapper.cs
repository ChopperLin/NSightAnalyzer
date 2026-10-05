using NsightAnalyzer.Contracts;
using NsightAnalyzer.Operations;

namespace NsightAnalyzer.Wrappers;

internal static class SourceHotspotsWrapper
{
    public static async Task<OperationResult> ExecuteAsync(
        string tracePath,
        string? viewerPath,
        int timeoutMs,
        int? eventOrdinal,
        string? eventPath,
        string shaderStage,
        string shaderHash,
        int shaderOccurrence,
        string sourceView,
        int sourceViewOccurrence,
        int cursor,
        int limit)
    {
        var inputError = ValidateQuery(sourceView, sourceViewOccurrence, cursor, limit);
        if (inputError is not null)
        {
            return OperationResult.Failure(inputError);
        }
        var context = new WrapperContext(new WrapperDeadline(timeoutMs));
        // Read one complete, bounded source snapshot. Paging a source atom repeatedly
        // here would reselect the shader and reload all its rows for every page.
        var result = await context.InvokeAtomAsync(remainingMs =>
            TraceShaderSourceOperation.ReadCompleteAsync(tracePath, viewerPath, remainingMs,
                eventOrdinal, eventPath, shaderStage, shaderHash, shaderOccurrence));
        if (!result.IsSuccess)
        {
            return result;
        }
        if (result.Value is not ShaderSourceValue source)
        {
            return OperationResult.Failure(WrapperSupport.InternalError(
                "The complete source read returned an unexpected value contract."));
        }

        context.AddRetrievedFacts(source.Rows.ReturnedCount);
        context.AddWarning(WrapperSupport.ShaderSampleWarning);
        var projected = Project(source, sourceView, sourceViewOccurrence, cursor, limit, context.Stats());
        return projected.IsSuccess
            ? OperationResult.Success(projected.Value!, context.Provenance, context.Warnings)
            : OperationResult.Failure(projected.Error!);
    }

    internal static WrapperValueResult<SourceHotspotsValue> Project(
        ShaderSourceValue source,
        string sourceView,
        int sourceViewOccurrence,
        int cursor,
        int limit,
        WrapperExecutionStats execution)
    {
        var inputError = ValidateQuery(sourceView, sourceViewOccurrence, cursor, limit);
        if (inputError is not null)
        {
            return WrapperValueResult<SourceHotspotsValue>.Failure(inputError);
        }
        var page = source.Rows;
        var pageError = WrapperSupport.ValidatePage(page, 0, page.Limit);
        if (pageError is not null || page.Truncated || page.ReturnedCount != page.TotalCount)
        {
            return WrapperValueResult<SourceHotspotsValue>.Failure(pageError ??
                WrapperSupport.InternalError("Source hotspot search requires complete source facts."));
        }
        var views = source.Views.Where(view =>
            view.Kind == sourceView && view.Occurrence == sourceViewOccurrence).ToArray();
        if (views.Length == 0)
        {
            var unavailable = sourceView == "hlsl" && source.HlslAvailability != "available";
            return WrapperValueResult<SourceHotspotsValue>.Failure(new(
                unavailable ? ErrorCategory.Unavailable : ErrorCategory.NotFound,
                unavailable ? "wrapper.source_view_unavailable" : "wrapper.source_view_not_found",
                unavailable ? "The requested HLSL view is unavailable for this shader."
                    : "The exact source view occurrence was not found for this shader.",
                $"view={sourceView}; occurrence={sourceViewOccurrence}; hlslAvailability={source.HlslAvailability}"));
        }
        if (views.Length != 1 ||
            page.Items.Select(row => row.SourceOrdinal).Distinct().Count() != page.Items.Count)
        {
            return WrapperValueResult<SourceHotspotsValue>.Failure(
                WrapperSupport.InternalError("Source view or source ordinal identity is not unique."));
        }

        var view = views[0];
        var viewRows = page.Items.Where(row =>
            row.ViewKind == sourceView && row.ViewOccurrence == sourceViewOccurrence).ToArray();
        if (view.SampleAvailability is not ("available" or "empty" or "notApplicable") ||
            viewRows.Any(row => row.SampleAvailability is not ("available" or "empty" or "notApplicable")))
        {
            return WrapperValueResult<SourceHotspotsValue>.Failure(new(
                ErrorCategory.Unavailable, "wrapper.source_samples_unavailable",
                "The selected source view has unavailable samples; complete hotspot coverage cannot be computed.",
                $"view={sourceView}; occurrence={sourceViewOccurrence}; sampleAvailability={view.SampleAvailability}"));
        }
        if (view.RowCount != viewRows.Length || view.AttributedSampleCount < 0 ||
            viewRows.Any(row => row.SourceOrdinal < 0 || row.SampleCount < 0) ||
            view.SampleAvailability != "available" && view.AttributedSampleCount != 0 ||
            viewRows.Any(row => row.SampleAvailability != "available" && row.SampleCount != 0) ||
            viewRows.Aggregate(0m, (sum, row) => sum + row.SampleCount) != view.AttributedSampleCount)
        {
            return WrapperValueResult<SourceHotspotsValue>.Failure(
                WrapperSupport.InternalError("Source rows do not close to their selected view sample total."));
        }

        var ordered = viewRows.Where(row => row.SampleCount > 0)
            .OrderByDescending(row => row.SampleCount)
            .ThenBy(row => row.SourceOrdinal)
            .ToArray();
        var selected = WrapperSupport.Page(ordered, cursor, limit);
        var returnedSamples = selected.Items.Sum(row => row.SampleCount);
        decimal? coverage = view.AttributedSampleCount > 0
            ? Math.Round(returnedSamples * 100m / view.AttributedSampleCount,
                6, MidpointRounding.AwayFromZero)
            : null;
        return WrapperValueResult<SourceHotspotsValue>.Success(new(
            source.Scope,
            source.Shader,
            new(sourceView, sourceViewOccurrence, "positiveSamples", "sampleCountDescendingThenSourceOrdinal"),
            view,
            source.HlslAvailability,
            returnedSamples,
            coverage,
            "returnedPageSamplesDividedBySelectedViewAttributedSamples",
            selected,
            execution));
    }

    private static OperationError? ValidateQuery(string view, int occurrence, int cursor, int limit) =>
        view is not ("dxil" or "sass" or "hlsl" or "source") || occurrence < 0 ||
        cursor < 0 || limit is < 1 or > ContractLimits.MaximumPageLimit
            ? new(ErrorCategory.InvalidInput, "wrapper.source_query_invalid",
                "Select an exact source view kind and occurrence, with valid paging.")
            : null;
}
