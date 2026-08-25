namespace NsightAnalyzer.Contracts;

public sealed record WrapperExecutionStats(
    int AtomCallCount,
    int RetrievedFactCount,
    int ScannedEventCount,
    long ElapsedMilliseconds);

public sealed record ResolveEventQuery(
    string ExactName,
    int Occurrence,
    int? WithinPreorderOrdinal);

public sealed record ResolveEventValue(
    ResolveEventQuery Query,
    EventFact? WithinScope,
    EventFact Event,
    IReadOnlyList<EventFact> Ancestors,
    int MatchCount,
    WrapperExecutionStats Execution);

public sealed record CompleteRangeMetrics(
    int TableCount,
    int RowCount,
    int TotalCount,
    IReadOnlyList<RangeMetricFact> Items);

public sealed record RankedRangeShaders(
    string Order,
    int TotalCount,
    int SampledCount,
    long TotalSampleCount,
    int ReturnedCount,
    long ReturnedSampleCount,
    decimal? SampleCoveragePercent,
    bool Truncated,
    IReadOnlyList<RangeShaderFact> Items);

public sealed record CompleteRangeInstructionMix(
    int TotalCount,
    IReadOnlyList<RangeInstructionMixFact> Items);

public sealed record InspectPassValue(
    EventFact Event,
    CompleteRangeMetrics Metrics,
    RankedRangeShaders Shaders,
    CompleteRangeInstructionMix InstructionMix,
    WrapperExecutionStats Execution);

public sealed record ComparedRangeEndpoint(
    string TraceFileName,
    string ArtifactIdentity,
    EventFact Event);

public sealed record DisplayDurationValue(
    string? DisplayValue,
    decimal? Milliseconds,
    string NumericState);

public sealed record DurationComparisonFact(
    string Field,
    string DeltaBasis,
    DisplayDurationValue? Target,
    DisplayDurationValue? Baseline,
    decimal? DeltaMilliseconds,
    decimal? RelativeDeltaPercent);

public sealed record RangeMetricComparisonIdentity(
    string TableName,
    int TableOccurrence,
    string RowName,
    int RowOccurrence,
    string ColumnName,
    int ColumnOccurrence,
    string? Unit);

public sealed record RangeMetricComparisonSide(
    int SourceOrdinal,
    object? Value,
    string ValueSource,
    string ValueKind,
    string Availability,
    decimal? NumericValue,
    string NumericState);

public sealed record RangeMetricDeltaFact(
    RangeMetricComparisonIdentity Identity,
    string JoinState,
    string? Description,
    string? BaselineDescription,
    RangeMetricComparisonSide? Target,
    RangeMetricComparisonSide? Baseline,
    decimal? Delta,
    decimal? RelativeDeltaPercent,
    bool Changed);

public sealed record RangeMetricTableComparisonFact(
    string TableName,
    int TableOccurrence,
    int TotalCount,
    int MatchedCount,
    int TargetOnlyCount,
    int BaselineOnlyCount,
    int NumericComparableCount,
    int ChangedCount);

public sealed record RangeMetricComparison(
    string DeltaDirection,
    int TotalCount,
    int MatchedCount,
    int TargetOnlyCount,
    int BaselineOnlyCount,
    int NumericComparableCount,
    int ChangedCount,
    IReadOnlyList<RangeMetricTableComparisonFact> Tables,
    Page<RangeMetricDeltaFact> Deltas);

public sealed record RangeShaderComparisonIdentity(
    string Stage,
    string Name,
    string? Hash,
    int HashOccurrence,
    string? Pipeline);

public sealed record RangeShaderComparisonSide(
    ShaderKey Key,
    long SampleCount,
    int? MaximumTheoreticalWarps,
    int? StaticRegisters,
    string? SharedMemory,
    string? CtaDimensions,
    int? LiveRegisters,
    int? StaticInstructionCount,
    long DependencyAttributedSampleCount,
    string? AverageWarpLatency,
    ShaderCorrelationInfo Correlation);

public sealed record RangeShaderDeltaFact(
    RangeShaderComparisonIdentity Identity,
    string JoinState,
    RangeShaderComparisonSide? Target,
    RangeShaderComparisonSide? Baseline,
    long? SampleCountDelta,
    int? MaximumTheoreticalWarpsDelta,
    int? StaticRegistersDelta,
    int? LiveRegistersDelta,
    int? StaticInstructionCountDelta,
    long? DependencyAttributedSampleCountDelta,
    decimal? RelativeSampleCountDeltaPercent,
    bool InstructionMixChanged,
    bool TopStallsChanged,
    bool CorrelationChanged,
    bool Changed);

public sealed record RangeShaderComparison(
    string DeltaDirection,
    string Order,
    int TotalCount,
    int MatchedCount,
    int TargetOnlyCount,
    int BaselineOnlyCount,
    int ChangedCount,
    long TargetTotalSampleCount,
    long BaselineTotalSampleCount,
    int ReturnedCount,
    bool Truncated,
    IReadOnlyList<RangeShaderDeltaFact> Items);

public sealed record RangeInstructionComparisonIdentity(
    string Pipe,
    string Family,
    string? Operation,
    int Occurrence);

public sealed record RangeInstructionComparisonSide(
    int SourceOrdinal,
    long SampleCount,
    long InstructionCount);

public sealed record RangeInstructionDeltaFact(
    RangeInstructionComparisonIdentity Identity,
    string JoinState,
    RangeInstructionComparisonSide? Target,
    RangeInstructionComparisonSide? Baseline,
    long? SampleCountDelta,
    long? InstructionCountDelta,
    decimal? RelativeSampleCountDeltaPercent,
    decimal? RelativeInstructionCountDeltaPercent,
    bool Changed);

public sealed record RangeInstructionComparison(
    string DeltaDirection,
    string Order,
    int TotalCount,
    int MatchedCount,
    int TargetOnlyCount,
    int BaselineOnlyCount,
    int ChangedCount,
    IReadOnlyList<RangeInstructionDeltaFact> Items);

public sealed record SummedStallComparisonFact(
    string Reason,
    long? TargetSummedSampleCount,
    int TargetContributingFactCount,
    long? BaselineSummedSampleCount,
    int BaselineContributingFactCount,
    long? SummedSampleCountDelta,
    decimal? RelativeSummedSampleCountDeltaPercent);

public sealed record SummedStallComparison(
    string Aggregation,
    string DeltaDirection,
    string Order,
    IReadOnlyList<SummedStallComparisonFact> Items);

public sealed record CompareRangesValue(
    ComparedRangeEndpoint Target,
    ComparedRangeEndpoint Baseline,
    IReadOnlyList<DurationComparisonFact> Durations,
    RangeMetricComparison Metrics,
    RangeShaderComparison Shaders,
    RangeInstructionComparison InstructionMix,
    SummedStallComparison Stalls,
    WrapperExecutionStats Execution);

public sealed record CompareFrameTimingQuery(
    int TargetFrameIndex,
    int TargetFrameEventOrdinal,
    int BaselineFrameIndex,
    int BaselineFrameEventOrdinal,
    int AnalysisSeedEventOrdinal,
    int PresentQueueEventOrdinal);

public sealed record ViewerTimelineTimestamp(
    string DisplayValue,
    decimal Milliseconds,
    decimal DisplayResolutionMilliseconds,
    string NumericState);

public sealed record FrameTimingDurationFact(
    string Field,
    string Basis,
    decimal Milliseconds,
    decimal DisplayResolutionMilliseconds,
    string NumericState);

public sealed record FrameTimingAlignmentFact(
    string Basis,
    decimal TraceAnalysisDurationMilliseconds,
    decimal PresentIntervalMilliseconds,
    decimal ResidualMilliseconds,
    decimal DisplayPrecisionToleranceMilliseconds,
    string State);

public sealed record FrameTimingEndpoint(
    int FrameIndex,
    TraceAnalysisRangeFact TraceAnalysisRange,
    EventFact SelectedFrameEvent,
    EventFact PreviousPresentEvent,
    EventFact PresentEvent,
    ViewerTimelineTimestamp PreviousPresentStart,
    ViewerTimelineTimestamp SelectedFrameStart,
    ViewerTimelineTimestamp SelectedFrameEnd,
    ViewerTimelineTimestamp PresentStart,
    IReadOnlyList<FrameTimingDurationFact> Durations,
    FrameTimingAlignmentFact TraceAnalysisAlignment);

public sealed record FrameTimingSequenceFact(
    EventFact PresentQueue,
    EventKey AnalysisSeedScope,
    string PresentScope,
    string PresentIdentity,
    string FrameJoin,
    int PresentEventCount,
    int TraceAnalysisFrameCount,
    string SelectedFrameEventIdentity,
    int SelectedFrameEventDepth,
    IReadOnlyList<int> SelectedFrameEventParentPath);

public sealed record FrameTimingDeltaFact(
    string Field,
    string DeltaDirection,
    decimal TargetMilliseconds,
    decimal BaselineMilliseconds,
    decimal DeltaMilliseconds,
    decimal? RelativeDeltaPercent,
    decimal DisplayResolutionMilliseconds);

public sealed record CompareFrameTimingValue(
    CompareFrameTimingQuery Query,
    FrameTimingSequenceFact Sequence,
    FrameTimingEndpoint Target,
    FrameTimingEndpoint Baseline,
    string DeltaOrder,
    IReadOnlyList<FrameTimingDeltaFact> Deltas,
    WrapperExecutionStats Execution);
