namespace NsightAnalyzer.Contracts;

public sealed record TimingPairInput(
    int TargetEventOrdinal, int BaselineEventOrdinal,
    string? Label = null, string? SampleGroup = null);
public sealed record TimingPairsInput(IReadOnlyList<TimingPairInput> Pairs);
public sealed record TimingPairFact(
    int SourceOrdinal, string? CallerLabel,
    ComparedRangeEndpoint Target, ComparedRangeEndpoint Baseline,
    IReadOnlyList<DurationComparisonFact> Durations,
    IReadOnlyList<OperationWarning> Warnings,
    string? CallerSampleGroup = null);
public sealed record TimingSampleContext(
    IReadOnlyList<int> ParentTreePath, string? Object, string? Thread);
public sealed record TimingSampleSummary(
    string EventDescription, int EventDepth, string Field,
    int PairCount, int ComparableCount, int UnavailableCount,
    decimal? MedianTargetMilliseconds, decimal? MedianBaselineMilliseconds,
    decimal? MedianDeltaMilliseconds, decimal? MinimumDeltaMilliseconds, decimal? MaximumDeltaMilliseconds,
    string GroupingBasis, string? CallerSampleGroup,
    TimingSampleContext? TargetContext, TimingSampleContext? BaselineContext);
public sealed record BatchTimingValue(
    string Pairing, string StatisticsBasis, Page<TimingPairFact> Pairs,
    IReadOnlyList<TimingSampleSummary> Summaries, WrapperExecutionStats Execution);
