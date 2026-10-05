namespace NsightAnalyzer.Contracts;

public sealed record FindMetricsQuery(
    string? NameContains,
    int? SourceOrdinal,
    IReadOnlyList<string> Tables,
    string MatchFields,
    string Order);

public sealed record FindMetricsValue(
    EventKey Scope,
    FindMetricsQuery Query,
    int TableCount,
    int RowCount,
    int ScannedMetricCount,
    Page<RangeMetricFact> Metrics,
    WrapperExecutionStats Execution);

public sealed record SourceHotspotsQuery(
    string View,
    int ViewOccurrence,
    string Filter,
    string Order);

public sealed record SourceHotspotsValue(
    EventKey Scope,
    ShaderSourceKey Shader,
    SourceHotspotsQuery Query,
    ShaderSourceViewFact View,
    string HlslAvailability,
    long ReturnedSampleCount,
    decimal? SampleCoveragePercent,
    string CoverageBasis,
    Page<ShaderSourceRowFact> Rows,
    WrapperExecutionStats Execution);
