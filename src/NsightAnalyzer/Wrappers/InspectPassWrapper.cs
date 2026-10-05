using NsightAnalyzer.Contracts;
using NsightAnalyzer.Operations;

namespace NsightAnalyzer.Wrappers;

internal sealed record PassInspectionData(
    EventFact Event,
    int MetricTableCount,
    int MetricRowCount,
    IReadOnlyList<RangeMetricFact> Metrics,
    IReadOnlyList<RangeShaderFact> Shaders,
    IReadOnlyList<RangeInstructionMixFact>? InstructionMix,
    OperationError? InstructionMixError,
    WrapperContext Context,
    RangeSections Sections);

internal static class InspectPassWrapper
{
    public static async Task<OperationResult> ExecuteAsync(
        string tracePath,
        string? viewerPath,
        int timeoutMs,
        int eventOrdinal,
        IReadOnlyList<string> metricTables,
        int topShaderCount,
        int metricCursor,
        int metricLimit,
        RangeSections sections)
    {
        var deadline = new WrapperDeadline(timeoutMs);
        var inspected = await InspectCoreAsync(
            tracePath,
            viewerPath,
            eventOrdinal,
            metricTables,
            sections,
            deadline: deadline);
        if (!inspected.IsSuccess)
        {
            return OperationResult.Failure(inspected.Error!);
        }

        var data = inspected.Value!;
        if (sections.HasFlag(RangeSections.Shaders))
        {
            data.Context.AddWarning(WrapperSupport.ShaderSampleWarning);
        }
        var value = Project(data, topShaderCount, metricCursor, metricLimit);
        return OperationResult.Success(
            value,
            data.Context.Provenance,
            data.Context.Warnings);
    }

    internal static async Task<WrapperValueResult<PassInspectionData>> InspectCoreAsync(
        string tracePath,
        string? viewerPath,
        int eventOrdinal,
        IReadOnlyList<string> metricTables,
        RangeSections sections,
        WrapperDeadline deadline)
    {
        var context = new WrapperContext(deadline);
        var eventResult = await GetExactEventAsync(
            context,
            tracePath,
            viewerPath,
            eventOrdinal);
        if (!eventResult.IsSuccess)
        {
            return WrapperValueResult<PassInspectionData>.Failure(eventResult.Error!);
        }
        var eventFact = eventResult.Value!;

        RangeMetricsValue? metrics = null;
        RangeShadersValue? shaders = null;
        IReadOnlyList<RangeInstructionMixFact>? instructions = null;
        if ((sections & (RangeSections.Metrics | RangeSections.Shaders)) != 0)
        {
            var bundleResult = await context.InvokeAtomAsync(
                remainingMs => RangeBundleReader.ReadAsync(
                    tracePath, viewerPath, remainingMs, eventOrdinal, metricTables, sections));
            if (!bundleResult.IsSuccess)
            {
                return WrapperValueResult<PassInspectionData>.Failure(bundleResult.Error!);
            }
            if (bundleResult.Value is not RangeBundle bundle ||
                (bundle.Metrics is not null) != sections.HasFlag(RangeSections.Metrics) ||
                (bundle.Shaders is not null) != sections.HasFlag(RangeSections.Shaders))
            {
                return WrapperValueResult<PassInspectionData>.Failure(
                    WrapperSupport.InternalError("The range bundle did not match the requested sections."));
            }
            metrics = bundle.Metrics;
            shaders = bundle.Shaders;
            foreach (var scope in new[] { metrics?.Scope, shaders?.Scope }.OfType<EventKey>())
            {
                if (!WrapperSupport.SameEventKey(eventFact.Key, scope))
                {
                    return WrapperValueResult<PassInspectionData>.Failure(
                        WrapperSupport.InternalError("A range section returned a different exact scope."));
                }
            }
            if (metrics?.Metrics.Truncated == true || shaders?.Shaders.Truncated == true)
            {
                return WrapperValueResult<PassInspectionData>.Failure(
                    WrapperSupport.InternalError("The range bundle did not return complete source pages."));
            }
            context.AddRetrievedFacts((metrics?.Metrics.ReturnedCount ?? 0) + (shaders?.Shaders.ReturnedCount ?? 0));
        }
        if (sections.HasFlag(RangeSections.InstructionMix))
        {
            var mix = await WrapperSupport.CollectPagesAsync<RangeInstructionMixValue, RangeInstructionMixFact>(
                context,
                (cursor, limit, remainingMs) => TraceRangeInstructionMixOperation.ExecuteAsync(
                    tracePath, viewerPath, remainingMs, eventOrdinal, null, cursor, limit),
                value => value.Instructions,
                value => WrapperSupport.SameEventKey(eventFact.Key, value.Scope)
                    ? null : WrapperSupport.InternalError("Instruction Mix returned a different exact scope."));
            if (!mix.IsSuccess)
            {
                return WrapperValueResult<PassInspectionData>.Failure(mix.Error!);
            }
            instructions = mix.Value!.Items;
        }
        return WrapperValueResult<PassInspectionData>.Success(new(
            eventFact, metrics?.TableCount ?? 0, metrics?.RowCount ?? 0,
            metrics?.Metrics.Items ?? [], shaders?.Shaders.Items ?? [],
            instructions, null, context, sections));
    }

    internal static InspectPassValue Project(
        PassInspectionData data,
        int topShaderCount,
        int metricCursor = 0,
        int metricLimit = ContractLimits.DefaultPageLimit)
    {
        var orderedShaders = data.Shaders
            .OrderByDescending(shader => shader.SampleCount)
            .ThenBy(shader => shader.Key.Stage, StringComparer.Ordinal)
            .ThenBy(shader => shader.Key.Hash, StringComparer.Ordinal)
            .ThenBy(shader => shader.Key.HashOccurrence)
            .ThenBy(shader => shader.Key.Name, StringComparer.Ordinal)
            .ThenBy(shader => shader.Key.Pipeline, StringComparer.Ordinal)
            .ToArray();
        var returnedShaders = orderedShaders.Take(topShaderCount).ToArray();
        var totalSamples = orderedShaders.Sum(shader => shader.SampleCount);
        var returnedSamples = returnedShaders.Sum(shader => shader.SampleCount);
        decimal? coverage = totalSamples > 0
            ? Math.Round(
                (decimal)returnedSamples * 100m / totalSamples,
                6,
                MidpointRounding.AwayFromZero)
            : null;

        return new(
            data.Event,
            RangeSectionNames.For(data.Sections),
            data.Sections.HasFlag(RangeSections.Metrics) ? new(
                data.MetricTableCount,
                data.MetricRowCount,
                WrapperSupport.Page(data.Metrics, metricCursor, metricLimit)) : null,
            data.Sections.HasFlag(RangeSections.Shaders) ? new(
                "sampleCountDescThenStableShaderIdentity",
                data.Shaders.Count,
                data.Shaders.Count(shader => shader.SampleCount > 0),
                totalSamples,
                returnedShaders.Length,
                returnedSamples,
                coverage,
                returnedShaders.Length < data.Shaders.Count,
                returnedShaders) : null,
            data.Sections.HasFlag(RangeSections.InstructionMix)
                ? ProjectInstructionMix(data.InstructionMix, data.InstructionMixError) : null,
            data.Context.Stats(scannedEventCount: 1));
    }

    internal static CompleteRangeInstructionMix ProjectInstructionMix(
        IReadOnlyList<RangeInstructionMixFact>? items,
        OperationError? error)
    {
        if ((items is null) == (error is null))
        {
            throw new InvalidOperationException(
                "Instruction Mix must be either available or carry one explicit error.");
        }

        if (items is not null)
        {
            return new(
                "available",
                items.Count,
                items.OrderBy(item => item.SourceOrdinal).ToArray(),
                null);
        }

        return new(
            error!.Code == "trace.range_instruction_mix_not_loaded"
                ? "notLoaded"
                : "unavailable",
            null,
            null,
            error);
    }

    private static async Task<WrapperValueResult<EventFact>> GetExactEventAsync(
        WrapperContext context,
        string tracePath,
        string? viewerPath,
        int eventOrdinal)
    {
        const int limit = 1;
        var atom = await context.InvokeAtomAsync(
            remainingMs => TraceEventsOperation.ExecuteAsync(
                tracePath,
                viewerPath,
                remainingMs,
                eventOrdinal,
                limit));
        if (!atom.IsSuccess)
        {
            return WrapperValueResult<EventFact>.Failure(atom.Error!);
        }
        if (atom.Value is not TraceEventsValue value)
        {
            return WrapperValueResult<EventFact>.Failure(
                WrapperSupport.InternalError(
                    "trace.events returned an unexpected value contract."));
        }

        var pageError = WrapperSupport.ValidatePage(
            value.Events,
            eventOrdinal,
            limit);
        if (pageError is not null)
        {
            return WrapperValueResult<EventFact>.Failure(pageError);
        }
        context.AddRetrievedFacts(value.Events.ReturnedCount);
        if (value.Events.Items.Count != 1 ||
            value.Events.Items[0].Key.PreorderOrdinal != eventOrdinal)
        {
            return WrapperValueResult<EventFact>.Failure(
                new(
                    ErrorCategory.NotFound,
                    "wrapper.event_not_found",
                    "The exact event preorder ordinal was not found.",
                    $"eventOrdinal={eventOrdinal}"));
        }
        return WrapperValueResult<EventFact>.Success(value.Events.Items[0]);
    }
}
