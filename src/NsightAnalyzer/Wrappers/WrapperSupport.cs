using System.Diagnostics;
using NsightAnalyzer.Contracts;

namespace NsightAnalyzer.Wrappers;

internal sealed class WrapperDeadline
{
    private readonly Stopwatch stopwatch = Stopwatch.StartNew();

    public WrapperDeadline(int timeoutMs)
    {
        TimeoutMs = timeoutMs;
    }

    public int TimeoutMs { get; }

    public long ElapsedMilliseconds => stopwatch.ElapsedMilliseconds;

    public bool TryGetRemainingMilliseconds(out int remainingMs)
    {
        var remaining = TimeoutMs - stopwatch.ElapsedMilliseconds;
        if (remaining <= 0)
        {
            remainingMs = 0;
            return false;
        }

        remainingMs = (int)Math.Min(int.MaxValue, remaining);
        return true;
    }
}

internal sealed class WrapperContext
{
    private readonly List<FactProvenance> provenance = [];
    private readonly List<OperationWarning> warnings = [];
    private FactProvenance? sourceIdentity;

    public WrapperContext(WrapperDeadline deadline)
    {
        Deadline = deadline;
    }

    public WrapperDeadline Deadline { get; }

    public int AtomCallCount { get; private set; }

    public int RetrievedFactCount { get; private set; }

    public string? ArtifactIdentity => sourceIdentity?.ArtifactIdentity;

    public FactProvenance? SourceIdentity => sourceIdentity;

    public IReadOnlyList<FactProvenance> Provenance => provenance;

    public IReadOnlyList<OperationWarning> Warnings => warnings;

    public async Task<OperationResult> InvokeAtomAsync(
        Func<int, Task<OperationResult>> invocation)
    {
        if (!Deadline.TryGetRemainingMilliseconds(out var remainingMs))
        {
            return TimeoutFailure();
        }

        AtomCallCount++;
        var result = await invocation(remainingMs);
        if (!result.IsSuccess)
        {
            // An atom that timed out did so against whatever was left of the
            // wrapper's budget, not against a limit the caller chose. Reporting
            // the atom's remainder verbatim reads as "the timeout was set to
            // 924ms" and invites raising a limit that was never the problem.
            return result.Error is { Category: ErrorCategory.Timeout }
                ? BudgetExhaustedFailure(result.Error)
                : result;
        }

        var provenanceError = AcceptProvenance(result.Provenance);
        if (provenanceError is not null)
        {
            return OperationResult.Failure(provenanceError);
        }

        foreach (var warning in result.Warnings ?? [])
        {
            AddWarning(warning);
        }
        return result;
    }

    public void AddRetrievedFacts(int count)
    {
        RetrievedFactCount = checked(RetrievedFactCount + count);
    }

    public void AddWarning(OperationWarning warning)
    {
        if (!warnings.Contains(warning))
        {
            warnings.Add(warning);
        }
    }

    public WrapperExecutionStats Stats(int scannedEventCount = 0) =>
        new(
            AtomCallCount,
            RetrievedFactCount,
            scannedEventCount,
            Deadline.ElapsedMilliseconds);

    private OperationError? AcceptProvenance(
        IReadOnlyList<FactProvenance>? atomProvenance)
    {
        if (atomProvenance is null || atomProvenance.Count == 0)
        {
            return new(
                ErrorCategory.Internal,
                "wrapper.provenance_missing",
                "A composed atom returned no source provenance.");
        }

        foreach (var item in atomProvenance)
        {
            sourceIdentity ??= item;
            if (!SameSourceSnapshot(sourceIdentity, item))
            {
                return new(
                    ErrorCategory.AdapterMismatch,
                    "wrapper.provenance_mismatch",
                    "Composed atoms did not return one exact trace and decoder identity.");
            }
            if (!provenance.Contains(item))
            {
                provenance.Add(item);
            }
        }
        return null;
    }

    private static bool SameSourceSnapshot(
        FactProvenance expected,
        FactProvenance observed) =>
        expected.Source == observed.Source &&
        expected.SupportLevel == observed.SupportLevel &&
        expected.ProducerVersion == observed.ProducerVersion &&
        expected.ProducerBuild == observed.ProducerBuild &&
        expected.ProducerSku == observed.ProducerSku &&
        expected.QtVersion == observed.QtVersion &&
        expected.BridgeVersion == observed.BridgeVersion &&
        expected.ArtifactIdentity == observed.ArtifactIdentity;

    private OperationResult TimeoutFailure() =>
        OperationResult.Failure(
            ErrorCategory.Timeout,
            "wrapper.timeout",
            "The deterministic wrapper exceeded its total runtime bound.",
            BudgetDetail(null));

    private OperationResult BudgetExhaustedFailure(OperationError atomError) =>
        OperationResult.Failure(
            ErrorCategory.Timeout,
            "wrapper.timeout",
            "The deterministic wrapper exceeded its total runtime bound.",
            BudgetDetail(atomError.Code));

    /// <summary>
    /// States the budget the caller actually set and how much work was done
    /// against it, so the reader can tell "raise the timeout" apart from
    /// "narrow the request".
    /// </summary>
    private string BudgetDetail(string? atomCode)
    {
        var detail =
            $"timeoutMs={Deadline.TimeoutMs}; " +
            $"elapsedMs={Deadline.ElapsedMilliseconds}; " +
            $"atomCalls={AtomCallCount}; " +
            $"retrievedFacts={RetrievedFactCount}; " +
            "the timeout bounds the whole wrapper, not one call";
        return atomCode is null ? detail : $"{detail}; exhaustedIn={atomCode}";
    }
}

internal sealed record WrapperValueResult<T>(T? Value, OperationError? Error)
    where T : class
{
    public bool IsSuccess => Value is not null;

    public static WrapperValueResult<T> Success(T value) => new(value, null);

    public static WrapperValueResult<T> Failure(OperationError error) => new(null, error);
}

internal sealed record CollectedPage<T>(
    IReadOnlyList<T> Items,
    int TotalCount);

internal static class WrapperSupport
{
    public static Page<T> Page<T>(IReadOnlyList<T> items, int cursor, int limit)
    {
        var returned = items.Skip(cursor).Take(limit).ToArray();
        int? next = (long)cursor + returned.Length < items.Count ? cursor + returned.Length : null;
        return new(returned, cursor, limit, items.Count, returned.Length, next is not null, next);
    }

    public const int AtomPageLimit = ContractLimits.MaximumPageLimit;

    public static readonly OperationWarning ShaderSampleWarning = new(
        "wrapper.shader_samples_are_attribution",
        "Shader sample counts are PC-sampling attribution candidates, not precise per-shader GPU time.");

    public static async Task<WrapperValueResult<CollectedPage<TItem>>> CollectPagesAsync<TValue, TItem>(
        WrapperContext context,
        Func<int, int, int, Task<OperationResult>> invoke,
        Func<TValue, Page<TItem>> selectPage,
        Func<TValue, OperationError?>? validateValue = null,
        Action<TValue>? observeValue = null)
        where TValue : class
    {
        var items = new List<TItem>();
        int? expectedTotal = null;
        var cursor = 0;
        while (true)
        {
            var result = await context.InvokeAtomAsync(
                timeoutMs => invoke(cursor, AtomPageLimit, timeoutMs));
            if (!result.IsSuccess)
            {
                return WrapperValueResult<CollectedPage<TItem>>.Failure(result.Error!);
            }
            if (result.Value is not TValue value)
            {
                return WrapperValueResult<CollectedPage<TItem>>.Failure(
                    InternalError("A composed atom returned an unexpected value contract."));
            }

            var valueError = validateValue?.Invoke(value);
            if (valueError is not null)
            {
                return WrapperValueResult<CollectedPage<TItem>>.Failure(valueError);
            }
            // Some atoms return per-page context alongside the paged items.
            // The caller sees every page, so nothing outside the page contract
            // is silently dropped on the way to the concatenated result.
            observeValue?.Invoke(value);

            var page = selectPage(value);
            var pageError = ValidatePage(page, cursor, AtomPageLimit, expectedTotal);
            if (pageError is not null)
            {
                return WrapperValueResult<CollectedPage<TItem>>.Failure(pageError);
            }

            expectedTotal ??= page.TotalCount;
            items.AddRange(page.Items);
            context.AddRetrievedFacts(page.ReturnedCount);
            if (page.NextCursor is null)
            {
                if (items.Count != expectedTotal.Value)
                {
                    return WrapperValueResult<CollectedPage<TItem>>.Failure(
                        InternalError("Concatenated atom pages did not close to totalCount."));
                }
                return WrapperValueResult<CollectedPage<TItem>>.Success(
                    new(items, expectedTotal.Value));
            }
            cursor = page.NextCursor.Value;
        }
    }

    public static OperationError? ValidatePage<T>(
        Page<T> page,
        int requestedCursor,
        int requestedLimit,
        int? expectedTotal = null)
    {
        if (page.Cursor != requestedCursor ||
            page.Limit != requestedLimit ||
            page.ReturnedCount != page.Items.Count ||
            page.TotalCount < 0 ||
            page.ReturnedCount < 0 ||
            page.ReturnedCount > requestedLimit ||
            (expectedTotal is not null && page.TotalCount != expectedTotal.Value))
        {
            return InternalError("A composed atom page violated its paging contract.");
        }

        var expectedNext = checked(page.Cursor + page.ReturnedCount);
        if (page.NextCursor is not null)
        {
            if (!page.Truncated || page.ReturnedCount == 0 ||
                page.NextCursor.Value != expectedNext ||
                page.NextCursor.Value >= page.TotalCount)
            {
                return InternalError("A composed atom returned an invalid next cursor.");
            }
        }
        else if (page.Truncated || expectedNext < page.TotalCount)
        {
            return InternalError("A composed atom ended before totalCount was closed.");
        }
        return null;
    }

    public static bool SameEventKey(EventKey left, EventKey right) =>
        left.PreorderOrdinal == right.PreorderOrdinal &&
        left.TreePath.SequenceEqual(right.TreePath) &&
        left.EventRange == right.EventRange &&
        left.Description == right.Description;

    public static bool IsStrictDescendant(
        IReadOnlyList<int> candidate,
        IReadOnlyList<int> ancestor) =>
        candidate.Count > ancestor.Count &&
        candidate.Take(ancestor.Count).SequenceEqual(ancestor);

    public static bool SameDecoder(
        FactProvenance left,
        FactProvenance right) =>
        left.Source == right.Source &&
        left.SupportLevel == right.SupportLevel &&
        left.ProducerVersion == right.ProducerVersion &&
        left.ProducerBuild == right.ProducerBuild &&
        left.ProducerSku == right.ProducerSku &&
        left.QtVersion == right.QtVersion &&
        left.BridgeVersion == right.BridgeVersion;

    public static IReadOnlyList<FactProvenance> MergeProvenance(
        params IReadOnlyList<FactProvenance>[] sources) =>
        sources.SelectMany(items => items).Distinct().ToArray();

    public static IReadOnlyList<OperationWarning> MergeWarnings(
        params IReadOnlyList<OperationWarning>[] sources) =>
        sources.SelectMany(items => items).Distinct().ToArray();

    public static OperationError InternalError(string message) =>
        new(
            ErrorCategory.Internal,
            "wrapper.contract_invalid",
            message);
}
