using NsightAnalyzer.Contracts;
using NsightAnalyzer.Operations;

namespace NsightAnalyzer.Wrappers;

internal static class FindRangesWrapper
{
    public static async Task<OperationResult> ExecuteAsync(
        string tracePath,
        string? viewerPath,
        int timeoutMs,
        string? nameContains,
        string grain,
        int? withinEventOrdinal,
        int cursor,
        int limit)
    {
        var context = new WrapperContext(new WrapperDeadline(timeoutMs));
        var result = await context.InvokeAtomAsync(remainingMs =>
            RangeCandidateOperation.ExecuteAsync(
                tracePath,
                viewerPath,
                remainingMs,
                nameContains,
                grain,
                withinEventOrdinal,
                cursor,
                limit));
        if (!result.IsSuccess)
        {
            return OperationResult.Failure(result.Error!);
        }
        if (result.Value is not RangeCandidatesValue value)
        {
            return OperationResult.Failure(
                WrapperSupport.InternalError(
                    "The range candidate operation returned an unexpected value contract."));
        }
        var pageError = WrapperSupport.ValidatePage(
            value.Ranges, cursor, limit);
        if (pageError is not null)
        {
            return OperationResult.Failure(pageError);
        }

        context.AddRetrievedFacts(
            value.Ranges.ReturnedCount + value.Ancestors.Count +
            (value.WithinScope is null ? 0 : 1));
        return OperationResult.Success(
            new FindRangesValue(
                new(
                    nameContains,
                    grain,
                    withinEventOrdinal,
                    "viewerDurationDescendingThenPreorderOrdinal"),
                value.WithinScope,
                value.Ancestors,
                value.Ranges,
                context.Stats(value.TotalEventCount)),
            context.Provenance,
            context.Warnings);
    }
}
