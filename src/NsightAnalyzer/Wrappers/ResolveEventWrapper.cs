using NsightAnalyzer.Contracts;
using NsightAnalyzer.Operations;

namespace NsightAnalyzer.Wrappers;

internal static class ResolveEventWrapper
{
    public static async Task<OperationResult> ExecuteAsync(
        string tracePath,
        string? viewerPath,
        int timeoutMs,
        string exactName,
        int occurrence,
        int? withinPreorderOrdinal)
    {
        var deadline = new WrapperDeadline(timeoutMs);
        var context = new WrapperContext(deadline);
        var cursor = withinPreorderOrdinal ?? 0;
        int? expectedTotal = null;
        var scannedEventCount = 0;
        var matchCount = 0;
        EventFact? withinScope = null;
        EventFact? selected = null;
        IReadOnlyList<EventFact>? selectedAncestors = null;
        var ancestorStack = new List<EventFact>();
        var scopeComplete = false;

        while (!scopeComplete)
        {
            var atom = await context.InvokeAtomAsync(
                remainingMs => TraceEventsOperation.ExecuteAsync(
                    tracePath,
                    viewerPath,
                    remainingMs,
                    cursor,
                    WrapperSupport.AtomPageLimit));
            if (!atom.IsSuccess)
            {
                return OperationResult.Failure(atom.Error!);
            }
            if (atom.Value is not TraceEventsValue value)
            {
                return OperationResult.Failure(
                    WrapperSupport.InternalError(
                        "trace.events returned an unexpected value contract."));
            }

            var page = value.Events;
            var pageError = WrapperSupport.ValidatePage(
                page,
                cursor,
                WrapperSupport.AtomPageLimit,
                expectedTotal);
            if (pageError is not null)
            {
                return OperationResult.Failure(pageError);
            }
            expectedTotal ??= page.TotalCount;
            context.AddRetrievedFacts(page.ReturnedCount);

            for (var index = 0; index < page.Items.Count; index++)
            {
                var fact = page.Items[index];
                var expectedOrdinal = checked(page.Cursor + index);
                if (fact.Key.PreorderOrdinal != expectedOrdinal || fact.Depth < 0)
                {
                    return OperationResult.Failure(
                        WrapperSupport.InternalError(
                            "trace.events did not preserve contiguous preorder hierarchy identity."));
                }

                if (withinPreorderOrdinal is not null)
                {
                    if (withinScope is null)
                    {
                        if (expectedOrdinal != withinPreorderOrdinal.Value)
                        {
                            return OperationResult.Failure(
                                WrapperSupport.InternalError(
                                    "trace.events did not return the requested scope root first."));
                        }

                        withinScope = fact;
                        ancestorStack.Add(fact);
                        scannedEventCount++;
                        continue;
                    }

                    if (!WrapperSupport.IsStrictDescendant(
                            fact.Key.TreePath,
                            withinScope.Key.TreePath))
                    {
                        scopeComplete = true;
                        break;
                    }

                    var relativeDepth = fact.Depth - withinScope.Depth;
                    if (relativeDepth <= 0 || relativeDepth > ancestorStack.Count)
                    {
                        return OperationResult.Failure(
                            WrapperSupport.InternalError(
                                "trace.events did not preserve the scoped preorder hierarchy."));
                    }

                    while (ancestorStack.Count > relativeDepth)
                    {
                        ancestorStack.RemoveAt(ancestorStack.Count - 1);
                    }
                }
                else
                {
                    if (fact.Depth > ancestorStack.Count)
                    {
                        return OperationResult.Failure(
                            WrapperSupport.InternalError(
                                "trace.events did not preserve contiguous preorder hierarchy identity."));
                    }

                    while (ancestorStack.Count > fact.Depth)
                    {
                        ancestorStack.RemoveAt(ancestorStack.Count - 1);
                    }
                }

                scannedEventCount++;
                if (fact.Key.Description.Equals(exactName, StringComparison.Ordinal))
                {
                    if (matchCount == occurrence)
                    {
                        selected = fact;
                        selectedAncestors = ancestorStack.ToArray();
                    }
                    matchCount++;
                }

                ancestorStack.Add(fact);
            }

            if (scopeComplete || page.NextCursor is null)
            {
                break;
            }
            cursor = page.NextCursor.Value;
        }

        if (withinPreorderOrdinal is not null && withinScope is null)
        {
            return OperationResult.Failure(
                ErrorCategory.NotFound,
                "wrapper.within_event_not_found",
                "The exact ancestor preorder ordinal was not found.",
                $"withinPreorderOrdinal={withinPreorderOrdinal.Value}");
        }
        if (selected is null)
        {
            // matches>0 with no selection means the name exists but the
            // occurrence index is out of range. Say so explicitly: occurrence
            // is zero-based, and "matches=1" otherwise reads as if a found
            // event was rejected.
            var detail = matchCount == 0
                ? $"exactName={exactName}; occurrence={occurrence}; matches=0; " +
                  "no event carries this exact name"
                : $"exactName={exactName}; occurrence={occurrence}; " +
                  $"matches={matchCount}; occurrence is zero-based, so the valid " +
                  $"range is 0..{matchCount - 1}";
            return OperationResult.Failure(
                ErrorCategory.NotFound,
                "wrapper.event_occurrence_not_found",
                "The requested exact event-name occurrence was not found.",
                detail);
        }

        var result = new ResolveEventValue(
            new(exactName, occurrence, withinPreorderOrdinal),
            withinScope,
            selected,
            selectedAncestors ?? [],
            matchCount,
            context.Stats(scannedEventCount));
        return OperationResult.Success(
            result,
            context.Provenance,
            context.Warnings);
    }
}
