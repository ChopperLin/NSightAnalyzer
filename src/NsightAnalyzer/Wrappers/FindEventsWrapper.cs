using NsightAnalyzer.Contracts;
using NsightAnalyzer.Operations;

namespace NsightAnalyzer.Wrappers;

internal static class FindEventsWrapper
{
    public static async Task<OperationResult> ExecuteAsync(
        string tracePath,
        string? viewerPath,
        int timeoutMs,
        string nameContains,
        int? withinEventOrdinal,
        int cursor,
        int limit)
    {
        var context = new WrapperContext(new WrapperDeadline(timeoutMs));
        var result = await context.InvokeAtomAsync(remainingMs =>
            EventCandidateOperation.ExecuteAsync(
                tracePath,
                viewerPath,
                remainingMs,
                nameContains,
                withinEventOrdinal,
                cursor,
                limit));
        if (!result.IsSuccess)
        {
            return OperationResult.Failure(result.Error!);
        }
        if (result.Value is not EventCandidatesValue value)
        {
            return OperationResult.Failure(
                WrapperSupport.InternalError(
                    "The event candidate operation returned an unexpected value contract."));
        }
        var pageError = WrapperSupport.ValidatePage(
            value.Events, cursor, limit);
        if (pageError is not null)
        {
            return OperationResult.Failure(pageError);
        }

        context.AddRetrievedFacts(
            value.Events.ReturnedCount + value.Ancestors.Count +
            (value.WithinScope is null ? 0 : 1));
        return OperationResult.Success(
            new FindEventsValue(
                new(nameContains, withinEventOrdinal, "preorderOrdinal"),
                value.WithinScope,
                value.Ancestors,
                value.Events,
                context.Stats(value.TotalEventCount)),
            context.Provenance,
            context.Warnings);
    }
}
