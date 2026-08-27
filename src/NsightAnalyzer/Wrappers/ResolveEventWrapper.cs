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

    /// <summary>
    /// Rebuilds the ancestor chain of one match from the returned ancestor set.
    /// <para>
    /// A tree path is the identity of a row's position, so an ancestor is any
    /// returned row whose path is a strict prefix of the match's. Selecting by
    /// path rather than by arrival order means the chain is correct even though
    /// the scan never visited the intervening rows.
    /// </para>
    /// </summary>
    private static IReadOnlyList<EventFact> AncestorsOf(
        EventFact match,
        IReadOnlyList<EventFact> ancestors,
        int? withinPreorderOrdinal) =>
        ancestors
            .Where(candidate =>
                WrapperSupport.IsStrictDescendant(
                    match.Key.TreePath, candidate.Key.TreePath) &&
                (withinPreorderOrdinal is null ||
                    candidate.Key.PreorderOrdinal >= withinPreorderOrdinal.Value))
            .OrderBy(candidate => candidate.Key.TreePath.Count)
            .ToArray();

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

        // Each page reports the ancestors of the matches on that page, so they
        // are merged by ordinal across pages rather than taken from the last
        // one. Identical ordinals are the same row.
        var ancestorsByOrdinal = new SortedDictionary<int, EventFact>();
        var totalEventCount = 0;

        // The name is matched inside the decoder. Paging the whole Event List
        // instead would re-traverse every event once per page, which on a large
        // report is hundreds of full traversals to find a handful of rows.
        var collected = await WrapperSupport.CollectPagesAsync<EventNameMatchesValue, EventFact>(
            context,
            (cursor, limit, remainingMs) => EventNameMatchOperation.ExecuteAsync(
                tracePath, viewerPath, remainingMs, exactName, contains, cursor, limit),
            value => value.Matches,
            observeValue: value =>
            {
                totalEventCount = value.TotalEventCount;
                foreach (var ancestor in value.Ancestors)
                {
                    ancestorsByOrdinal[ancestor.Key.PreorderOrdinal!.Value] = ancestor;
                }
            });
        if (!collected.IsSuccess)
        {
            return OperationResult.Failure(collected.Error!);
        }

        var ancestors = ancestorsByOrdinal.Values.ToArray();
        var allMatches = collected.Value!.Items;

        EventFact? withinScope = null;
        if (withinPreorderOrdinal is not null)
        {
            // The scope root is an ancestor of its matches, so it is already in
            // the returned set unless nothing under it matched.
            withinScope = ancestors
                .Concat(allMatches)
                .FirstOrDefault(fact =>
                    fact.Key.PreorderOrdinal == withinPreorderOrdinal.Value);
            if (withinScope is null)
            {
                return OperationResult.Failure(
                    ErrorCategory.NotFound,
                    "wrapper.within_event_not_found",
                    "The exact ancestor preorder ordinal was not found, or no " +
                    "event under it carries this name.",
                    $"withinPreorderOrdinal={withinPreorderOrdinal.Value}");
            }
        }

        // Occurrence indexes within the scope, so scoping is applied before
        // counting rather than after selecting.
        var matches = withinScope is null
            ? allMatches
            : allMatches
                .Where(fact => WrapperSupport.IsStrictDescendant(
                    fact.Key.TreePath, withinScope.Key.TreePath))
                .ToArray();

        if (contains)
        {
            var distinctNames = new SortedDictionary<string, EventFact>(StringComparer.Ordinal);
            var overflowNameCount = 0;
            foreach (var match in matches)
            {
                if (distinctNames.ContainsKey(match.Key.Description))
                {
                    continue;
                }
                if (distinctNames.Count < MaximumReportedCandidates)
                {
                    distinctNames.Add(match.Key.Description, match);
                }
                else
                {
                    overflowNameCount++;
                }
            }
            if (distinctNames.Count + overflowNameCount > 1)
            {
                // Never pick for the caller. Which of several distinct names is
                // the intended one is a judgement, not a fact. Each candidate is
                // reported with the ordinal of its first occurrence, so the
                // caller can proceed directly by ordinal without repeating the
                // scan.
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
        }

        if (occurrence >= matches.Count)
        {
            // matches>0 with no selection means the name exists but the
            // occurrence index is out of range. Say so explicitly: occurrence
            // is zero-based, and "matches=1" otherwise reads as if a found
            // event was rejected.
            var detail = matches.Count == 0
                ? $"exactName={exactName}; occurrence={occurrence}; matches=0; " +
                  "no event carries this exact name"
                : $"exactName={exactName}; occurrence={occurrence}; " +
                  $"matches={matches.Count}; occurrence is zero-based, so the valid " +
                  $"range is 0..{matches.Count - 1}";
            return OperationResult.Failure(
                ErrorCategory.NotFound,
                "wrapper.event_occurrence_not_found",
                "The requested exact event-name occurrence was not found.",
                detail);
        }

        var selected = matches[occurrence];
        var result = new ResolveEventValue(
            new(exactName, occurrence, withinPreorderOrdinal, contains ? "contains" : "exact"),
            withinScope,
            selected,
            AncestorsOf(selected, ancestors, withinPreorderOrdinal),
            matches.Count,
            context.Stats(totalEventCount),
            contains ? selected.Key.Description : null);
        return OperationResult.Success(
            result,
            context.Provenance,
            context.Warnings);
    }
}
