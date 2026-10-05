using System.Text.Json;
using System.Text.Json.Serialization;
using NsightAnalyzer.Adapters.NsightViewer2026_2;
using NsightAnalyzer.Contracts;

namespace NsightAnalyzer.Wrappers;

// Only caller-supplied exact pairs on the requested page are compared. Default
// summaries retain both exact parent/object/thread contexts. Cross-context sample
// groups require explicit caller metadata and never establish scope equivalence.
internal static class BatchTimingWrapper
{
    internal const int MaximumPairs = 32;
    internal const int MaximumInputBytes = 64 * 1024;
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8,
    };

    public static async Task<OperationResult> ExecuteAsync(
        string targetTracePath, string baselineTracePath, string pairsFile,
        string? viewerPath, int timeoutMs, int cursor, int limit)
    {
        var deadline = new WrapperDeadline(timeoutMs);
        var request = ReadPairs(pairsFile);
        if (!request.IsSuccess) return OperationResult.Failure(request.Error!);
        if (cursor < 0 || limit is < 1 or > ContractLimits.MaximumPageLimit)
            return OperationResult.Failure(ErrorCategory.InvalidInput, "wrapper.timing_page_invalid",
                "The timing batch requires a non-negative cursor and a bounded positive limit.");
        var selectedPairs = SelectPairs(request.Value!.Pairs, cursor, limit);
        var target = await TraceArtifactReader.OpenAsync(targetTracePath, "localWeak", timeoutMs);
        if (!target.IsSuccess) return OperationResult.Failure(target.Error!);
        var baseline = await TraceArtifactReader.OpenAsync(baselineTracePath, "localWeak", timeoutMs);
        if (!baseline.IsSuccess) return OperationResult.Failure(baseline.Error!);
        var results = new List<TimingPairFact>();
        var provenance = new List<FactProvenance>();
        var warnings = new List<OperationWarning>();
        var calls = 0;
        var retrieved = 0;
        foreach (var selected in selectedPairs.Items)
        {
            var pair = selected.Pair;
            if (!deadline.TryGetRemainingMilliseconds(out var remaining))
            {
                return OperationResult.Failure(ErrorCategory.Timeout, "wrapper.timeout",
                    "The timing batch exhausted its total deadline.", $"completedPairs={results.Count}");
            }
            var compared = await CompareRangesWrapper.ExecuteAsync(
                targetTracePath, baselineTracePath, viewerPath, remaining,
                pair.TargetEventOrdinal, pair.BaselineEventOrdinal, [], 5, 0, 20, RangeSections.Timing);
            if (!compared.IsSuccess) return compared;
            var value = (CompareRangesValue)compared.Value!;
            if (value.Target.ArtifactIdentity != target.Artifact!.ArtifactIdentity ||
                value.Baseline.ArtifactIdentity != baseline.Artifact!.ArtifactIdentity)
            {
                return OperationResult.Failure(ErrorCategory.Unavailable, "trace.file_changed",
                    "A report changed while the explicit timing pairs were compared.");
            }
            foreach (var source in compared.Provenance ?? [])
            {
                if (provenance.Count > 0 && !WrapperSupport.SameDecoder(provenance[0], source))
                    return OperationResult.Failure(ErrorCategory.AdapterMismatch, "wrapper.decoder_mismatch", "Timing pairs used different decoders.");
                if (!provenance.Contains(source)) provenance.Add(source);
            }
            foreach (var warning in compared.Warnings ?? [])
                if (!warnings.Contains(warning)) warnings.Add(warning);
            results.Add(new(selected.SourceOrdinal, pair.Label, value.Target, value.Baseline,
                value.Durations, compared.Warnings ?? [], pair.SampleGroup));
            calls = checked(calls + value.Execution.AtomCallCount);
            retrieved = checked(retrieved + value.Execution.RetrievedFactCount);
        }
        return OperationResult.Success(new BatchTimingValue("explicitCallerEventKeys",
            "currentPage",
            new(results, selectedPairs.Cursor, selectedPairs.Limit, selectedPairs.TotalCount,
                results.Count, selectedPairs.Truncated, selectedPairs.NextCursor), Summarize(results),
            new(calls, retrieved, results.Count * 2, deadline.ElapsedMilliseconds)), provenance, warnings);
    }

    internal static Page<(int SourceOrdinal, TimingPairInput Pair)> SelectPairs(
        IReadOnlyList<TimingPairInput> pairs, int cursor, int limit) =>
        WrapperSupport.Page(pairs.Select((pair, index) => (SourceOrdinal: index, Pair: pair)).ToArray(), cursor, limit);

    internal static WrapperValueResult<TimingPairsInput> ReadPairs(string path)
    {
        try
        {
            using var stream = new FileStream(Path.GetFullPath(path), FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is <= 0 or > MaximumInputBytes) return Invalid("The pair file must contain 1 to 65536 bytes.");
            Span<byte> prefix = stackalloc byte[4];
            var prefixLength = stream.Read(prefix);
            stream.Position = 0;
            if (prefixLength >= 2 && (prefix[0] == 0xff && prefix[1] == 0xfe || prefix[0] == 0xfe && prefix[1] == 0xff) ||
                prefixLength >= 4 && prefix[0] == 0 && prefix[1] == 0 && prefix[2] == 0xfe && prefix[3] == 0xff)
                return Invalid("The pair file must use UTF-8 (UTF-8 BOM is supported). Rewrite UTF-16/UTF-32 PowerShell output as UTF-8.");
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 8 });
            if (document.RootElement.ValueKind != JsonValueKind.Object || HasDuplicateFields(document.RootElement))
                return Invalid("The pair file must be an object without duplicate fields.");
            // Integer fields are required: a missing ordinal must never default to zero.
            if (!document.RootElement.TryGetProperty("pairs", out var array) || array.ValueKind != JsonValueKind.Array ||
                array.GetArrayLength() is < 1 or > MaximumPairs) return Invalid("pairs must contain 1 to 32 explicit pairs.");
            foreach (var pair in array.EnumerateArray())
            {
                if (pair.ValueKind != JsonValueKind.Object ||
                    HasDuplicateFields(pair) ||
                    !pair.TryGetProperty("targetEventOrdinal", out var a) || !a.TryGetInt32(out var ordinalA) || ordinalA < 0 ||
                    !pair.TryGetProperty("baselineEventOrdinal", out var b) || !b.TryGetInt32(out var ordinalB) || ordinalB < 0)
                    return Invalid("Each pair requires non-negative targetEventOrdinal and baselineEventOrdinal.");
            }
            var input = document.Deserialize<TimingPairsInput>(Options)!;
            if (input.Pairs.Any(pair => pair.Label is { Length: > 120 })) return Invalid("Caller labels are limited to 120 characters.");
            if (input.Pairs.Any(pair => pair.SampleGroup is not null &&
                    (string.IsNullOrWhiteSpace(pair.SampleGroup) || pair.SampleGroup.Length > 120)))
                return Invalid("Caller sampleGroup must contain 1 to 120 characters and cannot be whitespace-only.");
            if (input.Pairs.Select(pair => (pair.TargetEventOrdinal, pair.BaselineEventOrdinal)).Distinct().Count() != input.Pairs.Count)
                return Invalid("Repeated exact pairs would duplicate a sample; each pair must be unique.");
            return WrapperValueResult<TimingPairsInput>.Success(input);
        }
        catch (JsonException)
        {
            return Invalid("The pair file must contain valid UTF-8 JSON (UTF-8 BOM is supported). Check the JSON fields and rewrite UTF-16 output as UTF-8.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return Invalid($"The pair file could not be read: {exception.GetType().Name}.");
        }
    }

    internal static IReadOnlyList<TimingSampleSummary> Summarize(IReadOnlyList<TimingPairFact> pairs) =>
        pairs.Where(pair => pair.Target.Event.Key.Description == pair.Baseline.Event.Key.Description &&
                pair.Target.Event.Depth == pair.Baseline.Event.Depth)
            .GroupBy(GroupingKey)
            .OrderBy(group => group.Key.Description, StringComparer.Ordinal).ThenBy(group => group.Key.Depth)
            .ThenBy(group => group.Key.CallerSampleGroup, StringComparer.Ordinal)
            .ThenBy(group => group.Key.TargetParentPath, StringComparer.Ordinal)
            .ThenBy(group => group.Key.TargetObject, StringComparer.Ordinal)
            .ThenBy(group => group.Key.TargetThread, StringComparer.Ordinal)
            .ThenBy(group => group.Key.BaselineParentPath, StringComparer.Ordinal)
            .ThenBy(group => group.Key.BaselineObject, StringComparer.Ordinal)
            .ThenBy(group => group.Key.BaselineThread, StringComparer.Ordinal)
            .SelectMany(group => new[] { "duration", "gpuDuration", "cpuDuration" }.Select(field =>
            {
                var values = group.Select(pair => pair.Durations.Single(item => item.Field == field))
                    .Where(item => item.DeltaMilliseconds is not null && item.Target?.Milliseconds is not null && item.Baseline?.Milliseconds is not null)
                    .ToArray();
                var deltas = values.Select(item => item.DeltaMilliseconds!.Value).ToArray();
                var first = group.First();
                return new TimingSampleSummary(group.Key.Description, group.Key.Depth, field,
                    group.Count(), values.Length, group.Count() - values.Length,
                    Median(values.Select(item => item.Target!.Milliseconds!.Value)),
                    Median(values.Select(item => item.Baseline!.Milliseconds!.Value)), Median(deltas),
                    deltas.Length == 0 ? null : deltas.Min(), deltas.Length == 0 ? null : deltas.Max(),
                    group.Key.CallerSampleGroup is null ? "exactTargetAndBaselineContext" : "callerDeclaredSampleGroup",
                    group.Key.CallerSampleGroup,
                    group.Key.CallerSampleGroup is null ? Context(first.Target.Event) : null,
                    group.Key.CallerSampleGroup is null ? Context(first.Baseline.Event) : null);
            })).ToArray();

    private static SampleGroupingKey GroupingKey(TimingPairFact pair) =>
        new(pair.Target.Event.Key.Description, pair.Target.Event.Depth, pair.CallerSampleGroup,
            pair.CallerSampleGroup is null ? ParentPath(pair.Target.Event) : null,
            pair.CallerSampleGroup is null ? pair.Target.Event.Object : null,
            pair.CallerSampleGroup is null ? pair.Target.Event.Thread : null,
            pair.CallerSampleGroup is null ? ParentPath(pair.Baseline.Event) : null,
            pair.CallerSampleGroup is null ? pair.Baseline.Event.Object : null,
            pair.CallerSampleGroup is null ? pair.Baseline.Event.Thread : null);

    private static string ParentPath(EventFact fact) => string.Join('.', fact.Key.TreePath.SkipLast(1));
    private static TimingSampleContext Context(EventFact fact) =>
        new(fact.Key.TreePath.SkipLast(1).ToArray(), fact.Object, fact.Thread);

    private sealed record SampleGroupingKey(
        string Description, int Depth, string? CallerSampleGroup,
        string? TargetParentPath, string? TargetObject, string? TargetThread,
        string? BaselineParentPath, string? BaselineObject, string? BaselineThread);

    private static decimal? Median(IEnumerable<decimal> values)
    {
        var ordered = values.Order().ToArray();
        if (ordered.Length == 0) return null;
        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 1 ? ordered[middle] : ordered[middle - 1] / 2m + ordered[middle] / 2m;
    }
    private static bool HasDuplicateFields(JsonElement value) =>
        value.EnumerateObject().Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != value.EnumerateObject().Count();
    private static WrapperValueResult<TimingPairsInput> Invalid(string message) =>
        WrapperValueResult<TimingPairsInput>.Failure(new(ErrorCategory.InvalidInput, "wrapper.timing_pairs_invalid", message));
}
