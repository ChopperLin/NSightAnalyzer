using NsightAnalyzer.Contracts;

namespace NsightAnalyzer.Cli;

// Named, deterministic views of already verified facts. No metric selection,
// diagnosis, or change to collection coverage belongs in this projection.
internal static class AgentResponseProjection
{
    public static OperationResult Apply(OperationResult result, bool detail)
    {
        if (!result.IsSuccess || detail)
        {
            return result;
        }
        object? value = result.Value switch
        {
            RangeMetricsValue metrics => metrics with { Metrics = Metrics(metrics.Metrics) },
            FindMetricsValue metrics => metrics with { Metrics = Metrics(metrics.Metrics) },
            RangeShadersValue shaders => new
            {
                shaders.Scope,
                Shaders = Map(shaders.Shaders, Shader),
            },
            InspectPassValue pass => new
            {
                pass.Event,
                pass.Sections,
                Metrics = pass.Metrics is null ? null : pass.Metrics with
                {
                    Values = Metrics(pass.Metrics.Values),
                },
                Shaders = pass.Shaders is null ? null : new
                {
                    pass.Shaders.Order,
                    pass.Shaders.TotalCount,
                    pass.Shaders.SampledCount,
                    pass.Shaders.TotalSampleCount,
                    pass.Shaders.ReturnedCount,
                    pass.Shaders.ReturnedSampleCount,
                    pass.Shaders.SampleCoveragePercent,
                    pass.Shaders.Truncated,
                    Items = pass.Shaders.Items.Select(Shader).ToArray(),
                },
                pass.InstructionMix,
                pass.Execution,
            },
            CompareRangesValue comparison => new
            {
                comparison.Target,
                comparison.Baseline,
                comparison.Durations,
                comparison.Sections,
                Metrics = comparison.Metrics is null ? null : new
                {
                    comparison.Metrics.DeltaDirection,
                    comparison.Metrics.DeltaFilter,
                    comparison.Metrics.TotalCount,
                    comparison.Metrics.MatchedCount,
                    comparison.Metrics.TargetOnlyCount,
                    comparison.Metrics.BaselineOnlyCount,
                    comparison.Metrics.NumericComparableCount,
                    comparison.Metrics.ChangedCount,
                    Deltas = Map(comparison.Metrics.Deltas, item => item with
                    {
                        Description = null,
                        BaselineDescription = null,
                    }),
                },
                comparison.Shaders,
                comparison.InstructionMix,
                comparison.Stalls,
                comparison.Execution,
            },
            _ => result.Value,
        };
        return result with { Value = value };
    }

    private static Page<RangeMetricFact> Metrics(Page<RangeMetricFact> page) =>
        Map(page, item => item with { Description = null });

    private static object Shader(RangeShaderFact shader) => new
    {
        shader.Key,
        shader.SampleCount,
        shader.SampleAvailability,
        shader.MaximumTheoreticalWarps,
        shader.StaticRegisters,
        shader.LiveRegisters,
        shader.StaticInstructionCount,
        shader.SharedMemory,
        shader.CtaDimensions,
        shader.DependencyAttributedSampleCount,
        shader.DependencySampleAvailability,
        shader.AverageWarpLatency,
        shader.Correlation,
        shader.TopStalls,
    };

    private static Page<TOut> Map<TIn, TOut>(Page<TIn> page, Func<TIn, TOut> project) =>
        new(page.Items.Select(project).ToArray(), page.Cursor, page.Limit,
            page.TotalCount, page.ReturnedCount, page.Truncated, page.NextCursor);
}
