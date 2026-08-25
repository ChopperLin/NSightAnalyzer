using NsightAnalyzer.Contracts;
using NsightAnalyzer.Operations;

namespace NsightAnalyzer.Wrappers;

internal static class ResolveEventWrapper
{
    /// <summary>
    /// Distinct candidate names listed back when a substring query is ambiguous.
    /// Bounded so a broad query cannot produce an unbounded error payload.
    /// </summary>
    private const int MaximumReportedCandidates = 20;

    private static bool IsMatch(string description, string query, bool contains) =>
        contains
            ? description.Contains(query, StringComparison.OrdinalIgnoreCase)
            : description.Equals(query, StringComparison.Ordinal);

    /// <summary>
    /// Shortens one candidate name for the ambiguity message. D3D12 call text
    /// runs to hundreds of characters, which would bury the ordinals that make
    /// the message actionable.
    /// </summary>
    private static string Summarize(string description)
    {
        const int maximumLength = 72;
        var single = description.ReplaceLineEndings(" ");
        return single.Length <= maximumLength
            ? single
            : single[..maximumLength] + "...";
    }

    public static async Task<OperationResult> ExecuteAsync(
        string tracePath,
        string? viewerPath,
        int timeoutMs,
        string exactName,
        int occurrence,
        int? withinPreorderOrdinal,
        bool contains = false)
    {
        var deadline = new WrapperDeadline(timeoutMs);
        var context = new WrapperContext(deadline);
        var cursor = withinPreorderOrdinal ?? 0;
        int? expectedTotal = null;
        var scannedEventCount = 0;
        var matchCount = 0;
        var distinctNames = new SortedDictionary<string, EventFact>(StringComparer.Ordinal);
        var overflowNameCount = 0;
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
                if (IsMatch(fact.Key.Description, exactName, contains))
                {
                    // Under substring matching the same query can span several
                    // distinct names. Occurrence indexes within one name, so the
                    // distinct set is tracked and an ambiguous query is refused
                    // rather than silently resolved to whichever came first.
                    // The first ordinal per name is kept so the refusal can name
                    // an exact next call instead of only reporting a conflict.
                    if (distinctNames.Count < MaximumReportedCandidates &&
                        !distinctNames.ContainsKey(fact.Key.Description))
                    {
                        distinctNames.Add(fact.Key.Description, fact);
                    }
                    else if (!distinctNames.ContainsKey(fact.Key.Description))
                    {
                        overflowNameCount++;
                    }
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
        if (contains && distinctNames.Count + overflowNameCount > 1)
        {
            // Never pick for the caller. Which of several distinct names is the
            // intended one is a judgement, not a fact. Each candidate is
            // reported with the ordinal of its first occurrence, so the caller
            // can proceed directly by ordinal without repeating the scan.
            var candidates = distinctNames.Values.Select(candidate =>
                $"{candidate.Key.PreorderOrdinal}={Summarize(candidate.Key.Description)}");
            var suffix = overflowNameCount > 0
                ? $" (+{overflowNameCount} more)"
                : string.Empty;
            return OperationResult.Failure(
                ErrorCategory.InvalidInput,
                "wrapper.event_name_ambiguous",
                "The substring query matched more than one distinct event name.",
                $"query={exactName}; " +
                $"distinctNames={distinctNames.Count + overflowNameCount}; " +
                $"firstOrdinalPerName={string.Join(" | ", candidates)}{suffix}; " +
                "re-run with an exact --event-name, or pass one of these ordinals " +
                "to --event-ordinal");
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
            new(exactName, occurrence, withinPreorderOrdinal, contains ? "contains" : "exact"),
            withinScope,
            selected,
            selectedAncestors ?? [],
            matchCount,
            context.Stats(scannedEventCount),
            contains ? selected.Key.Description : null);
        return OperationResult.Success(
            result,
            context.Provenance,
            context.Warnings);
    }
}
