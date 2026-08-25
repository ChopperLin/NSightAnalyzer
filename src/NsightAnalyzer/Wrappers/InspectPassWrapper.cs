using NsightAnalyzer.Contracts;
using NsightAnalyzer.Operations;

namespace NsightAnalyzer.Wrappers;

internal sealed record PassInspectionData(
    EventFact Event,
    int MetricTableCount,
    int MetricRowCount,
    IReadOnlyList<RangeMetricFact> Metrics,
    IReadOnlyList<RangeShaderFact> Shaders,
    IReadOnlyList<RangeInstructionMixFact> InstructionMix,
    WrapperContext Context);

internal static class InspectPassWrapper
{
    public static async Task<OperationResult> ExecuteAsync(
        string tracePath,
        string? viewerPath,
        int timeoutMs,
        int eventOrdinal,
        IReadOnlyList<string> metricTables,
        int topShaderCount)
    {
        var deadline = new WrapperDeadline(timeoutMs);
        var inspected = await InspectCoreAsync(
            tracePath,
            viewerPath,
            eventOrdinal,
            metricTables,
            deadline);
        if (!inspected.IsSuccess)
        {
            return OperationResult.Failure(inspected.Error!);
        }

        var data = inspected.Value!;
        data.Context.AddWarning(WrapperSupport.ShaderSampleWarning);
        var value = Project(data, topShaderCount);
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

        int? tableCount = null;
        int? rowCount = null;
        var metrics = await WrapperSupport.CollectPagesAsync<RangeMetricsValue, RangeMetricFact>(
            context,
            (cursor, limit, remainingMs) => TraceRangeMetricsOperation.ExecuteAsync(
                tracePath,
                viewerPath,
                remainingMs,
                eventOrdinal,
                null,
                metricTables,
                cursor,
                limit),
            value => value.Metrics,
            value =>
            {
                if (!WrapperSupport.SameEventKey(eventFact.Key, value.Scope))
                {
                    return WrapperSupport.InternalError(
                        "trace.range-metrics returned a different exact scope.");
                }
                tableCount ??= value.TableCount;
                rowCount ??= value.RowCount;
                return tableCount == value.TableCount && rowCount == value.RowCount
                    ? null
                    : WrapperSupport.InternalError(
                        "trace.range-metrics metadata changed between pages.");
            });
        if (!metrics.IsSuccess)
        {
            return WrapperValueResult<PassInspectionData>.Failure(metrics.Error!);
        }

        var shaders = await WrapperSupport.CollectPagesAsync<RangeShadersValue, RangeShaderFact>(
            context,
            (cursor, limit, remainingMs) => TraceRangeShadersOperation.ExecuteAsync(
                tracePath,
                viewerPath,
                remainingMs,
                eventOrdinal,
                null,
                null,
                null,
                cursor,
                limit),
            value => value.Shaders,
            value => WrapperSupport.SameEventKey(eventFact.Key, value.Scope)
                ? null
                : WrapperSupport.InternalError(
                    "trace.range-shaders returned a different exact scope."));
        if (!shaders.IsSuccess)
        {
            return WrapperValueResult<PassInspectionData>.Failure(shaders.Error!);
        }

        var instructions = await WrapperSupport
            .CollectPagesAsync<RangeInstructionMixValue, RangeInstructionMixFact>(
                context,
                (cursor, limit, remainingMs) =>
                    TraceRangeInstructionMixOperation.ExecuteAsync(
                        tracePath,
                        viewerPath,
                        remainingMs,
                        eventOrdinal,
                        null,
                        cursor,
                        limit),
                value => value.Instructions,
                value => WrapperSupport.SameEventKey(eventFact.Key, value.Scope)
                    ? null
                    : WrapperSupport.InternalError(
                        "trace.range-instruction-mix returned a different exact scope."));
        if (!instructions.IsSuccess)
        {
            return WrapperValueResult<PassInspectionData>.Failure(instructions.Error!);
        }

        return WrapperValueResult<PassInspectionData>.Success(
            new(
                eventFact,
                tableCount ?? 0,
                rowCount ?? 0,
                metrics.Value!.Items,
                shaders.Value!.Items,
                instructions.Value!.Items,
                context));
    }

    internal static InspectPassValue Project(
        PassInspectionData data,
        int topShaderCount)
    {
        var orderedShaders = data.Shaders
            .OrderByDescending(shader => shader.SampleCount)
            .ThenBy(shader => shader.Key.Stage, StringComparer.Ordinal)
            .ThenBy(shader => shader.Key.Hash, StringComparer.Ordinal)
            .ThenBy(shader => shader.Key.HashOccurrence)
            .ThenBy(shader => shader.Key.Name, StringComparer.Ordinal)
            .ThenBy(shader => shader.Key.Pipeline, StringComparer.Ordinal)
            .ThenBy(shader => shader.Key.PreorderOrdinal)
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
            new(
                data.MetricTableCount,
                data.MetricRowCount,
                data.Metrics.Count,
                data.Metrics),
            new(
                "sampleCountDescThenStableShaderIdentity",
                data.Shaders.Count,
                data.Shaders.Count(shader => shader.SampleCount > 0),
                totalSamples,
                returnedShaders.Length,
                returnedSamples,
                coverage,
                returnedShaders.Length < data.Shaders.Count,
                returnedShaders),
            new(
                data.InstructionMix.Count,
                data.InstructionMix
                    .OrderBy(item => item.SourceOrdinal)
                    .ToArray()),
            data.Context.Stats(scannedEventCount: 1));
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
