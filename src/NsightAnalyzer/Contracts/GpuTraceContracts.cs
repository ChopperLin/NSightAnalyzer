namespace NsightAnalyzer.Contracts;

public sealed record TraceArtifactInfo(
    string FileName,
    long ByteLength,
    DateTime LastWriteTimeUtc,
    string IdentityMode,
    string ArtifactIdentity,
    string? Sha256);

public sealed record ViewerDecoderInfo(
    string Adapter,
    string SupportLevel,
    string ProductVersion,
    string ProductBuild,
    string ProductSku,
    string QtVersion,
    string BridgeVersion,
    string SourceSchema);

public sealed record TraceInfoValue(
    TraceArtifactInfo Trace,
    ViewerDecoderInfo Decoder,
    IReadOnlyList<string> AtomicFactFamilies);

public sealed record EventKey(
    int? PreorderOrdinal,
    IReadOnlyList<int> TreePath,
    string? EventRange,
    string Description);

public sealed record EventFact(
    EventKey Key,
    int Depth,
    int ChildCount,
    string? Object,
    string? Thread,
    string? CpuDuration,
    string? GpuDuration,
    string? Start,
    string? End,
    string? Duration);

public sealed record TraceEventsValue(
    Page<EventFact> Events);

/// <summary>
/// One range-grain row of the trace skeleton. Grain distinguishes the D3D12
/// containers that structure the capture from the caller's own instrumentation;
/// both are returned so the caller, not the wrapper, decides what to look at.
/// </summary>
public sealed record OutlineFact(
    EventKey Key,
    int Depth,
    int ChildCount,
    string Grain,
    string? Start,
    string? End,
    string? Duration);

public sealed record TraceOutlineValue(
    int TotalEventCount,
    Page<OutlineFact> Ranges);

/// <summary>
/// Events matching one name, plus every ancestor of those matches. Ancestors
/// are carried separately because they are context, not matches: counting them
/// as results would corrupt the occurrence index the caller addresses by.
/// </summary>
public sealed record EventNameMatchesValue(
    int TotalEventCount,
    IReadOnlyList<EventFact> Ancestors,
    Page<EventFact> Matches);

public sealed record EventParameterFact(
    IReadOnlyList<string> Path,
    string Name,
    object? Value,
    string ValueKind,
    string? DeclaredType,
    int Depth);

public sealed record EventParametersValue(
    EventKey Scope,
    string Command,
    IReadOnlyList<EventParameterFact> Parameters);

public sealed record RangeMetricFact(
    string TableName,
    int TableOccurrence,
    string RowName,
    int RowOccurrence,
    string ColumnName,
    int ColumnOccurrence,
    int SourceOrdinal,
    object? DisplayValue,
    object? PreciseValue,
    string ValueKind,
    string? Unit,
    string? Description,
    string Availability);

public sealed record RangeMetricsValue(
    EventKey Scope,
    int TableCount,
    int RowCount,
    Page<RangeMetricFact> Metrics);

public sealed record RangeMetricColumnFact(
    int ColumnIndex,
    string Name,
    int NameOccurrence,
    string? Unit);

public sealed record RangeMetricTableFact(
    string Name,
    int NameOccurrence,
    int SourceOrdinal,
    int RowCount,
    string Availability,
    IReadOnlyList<RangeMetricColumnFact> Columns);

public sealed record RangeMetricCatalogValue(
    EventKey Scope,
    Page<RangeMetricTableFact> Tables);

public sealed record ShaderKey(
    int PreorderOrdinal,
    IReadOnlyList<int> TreePath,
    string Stage,
    string Name,
    string? Hash,
    int HashOccurrence,
    string? Pipeline);

public sealed record ShaderCorrelationInfo(
    string Availability,
    string? SourceKind,
    string? FileName,
    string? MissingPdb,
    bool SassDebugInfoAvailable,
    bool SassToIlLineTableAvailable);

public sealed record ShaderInstructionCategoryFact(
    string Pipe,
    string Operation,
    long InstructionCount);

public sealed record ShaderStallFact(
    int Rank,
    string Reason,
    long SampleCount);

public sealed record RangeShaderFact(
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
    ShaderCorrelationInfo Correlation,
    IReadOnlyList<ShaderInstructionCategoryFact> InstructionMix,
    IReadOnlyList<ShaderStallFact> TopStalls);

public sealed record RangeShadersValue(
    EventKey Scope,
    Page<RangeShaderFact> Shaders);

public sealed record ShaderProfileValue(
    EventKey Scope,
    RangeShaderFact Shader);

public sealed record RangeInstructionMixFact(
    int SourceOrdinal,
    string Pipe,
    string Family,
    string? Operation,
    long SampleCount,
    long InstructionCount,
    IReadOnlyList<ShaderStallFact> Stalls);

public sealed record RangeInstructionMixValue(
    EventKey Scope,
    Page<RangeInstructionMixFact> Instructions);

public sealed record ShaderSourceKey(
    IReadOnlyList<int> TreePath,
    string Stage,
    string Name,
    string Hash,
    int HashOccurrence,
    string Pipeline);

public sealed record ShaderSourceViewFact(
    string Kind,
    int Occurrence,
    string Label,
    int RowCount,
    long AttributedSampleCount,
    long? InstructionCount,
    int AddressRowCount,
    string OpcodeTextAvailability);

public sealed record ShaderSourceRowFact(
    int SourceOrdinal,
    string ViewKind,
    int ViewOccurrence,
    int RowOrdinal,
    IReadOnlyList<int> TreePath,
    string RowKind,
    int? LineNumber,
    string? Address,
    string? SourceText,
    long SampleCount,
    long? InstructionCount,
    long? DependencyAttributedSampleCount,
    int? LiveRegisters,
    string? Pipe,
    string? Family,
    string? Operation,
    IReadOnlyList<ShaderStallFact> Stalls);

public sealed record ShaderSourceValue(
    EventKey Scope,
    ShaderSourceKey Shader,
    IReadOnlyList<ShaderSourceViewFact> Views,
    string HlslAvailability,
    Page<ShaderSourceRowFact> Rows);

public sealed record TraceAnalysisIssueFact(
    int Rank,
    string Name,
    decimal? ScorePercent);

public sealed record TraceAnalysisRangeFact(
    int SourceOrdinal,
    IReadOnlyList<int> TreePath,
    int? FrameIndex,
    string RangeName,
    decimal? DurationMs,
    decimal? SharePercent,
    string? FrameGain,
    IReadOnlyList<TraceAnalysisIssueFact> TopIssues);

public sealed record TraceAnalysisAnnotationFact(
    int SourceOrdinal,
    string Name,
    int NameOccurrence,
    object? Value,
    string? Description,
    string? Id,
    string Availability);

public sealed record TraceAnalysisValue(
    EventKey SeedScope,
    Page<TraceAnalysisRangeFact> Ranges,
    string AnnotationAvailability,
    IReadOnlyList<TraceAnalysisAnnotationFact> Annotations);

public sealed record CounterExportInfo(
    string Directory,
    string Format,
    string RangeDataFile,
    int RangeCount,
    int CounterCount,
    string TraceCopyCleanup,
    IReadOnlyList<string> EvidenceFiles);

public sealed record CounterColumnFact(
    int ColumnIndex,
    string Name,
    int NameOccurrence,
    string? Unit);

public sealed record CounterCatalogValue(
    EventKey SeedScope,
    CounterExportInfo Export,
    Page<CounterColumnFact> Counters);

public sealed record RangeCounterValueFact(
    int SourceOrdinal,
    int RowIndex,
    string RangeName,
    int RangeOccurrence,
    int ColumnIndex,
    string CounterName,
    int CounterOccurrence,
    string? RawValue,
    double? NumericValue,
    string ValueKind,
    string Availability);

public sealed record RangeCountersValue(
    EventKey SeedScope,
    CounterExportInfo Export,
    int SelectedRangeCount,
    int SelectedCounterCount,
    Page<RangeCounterValueFact> Values);
