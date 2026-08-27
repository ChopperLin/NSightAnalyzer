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
            includeInstructionMix: false,
            deadline: deadline);
        if (!inspected.IsSuccess)
        {
            return OperationResult.Failure(inspected.Error!);
        }

        var data = inspected.Value!;
        data.Context.AddWarning(WrapperSupport.ShaderSampleWarning);
        if (data.InstructionMixError is not null)
        {
            data.Context.AddWarning(new(
                "wrapper.instruction_mix_unavailable",
                "Instruction Mix is unavailable for this pass; complete metrics and shaders are still returned."));
        }
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
        bool includeInstructionMix,
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

        // Metrics and the shader inventory hang off the same event selection,
        // so they are read in one Viewer request. inspect-pass/v1 leaves the
        // independently stateful Instruction Mix model to its explicit atom;
        // compare-ranges opts into the strict uncached bundle below.
        var bundleResult = await context.InvokeAtomAsync(
            remainingMs => RangeBundleReader.ReadAsync(
                tracePath,
                viewerPath,
                remainingMs,
                eventOrdinal,
                metricTables,
                includeInstructionMix));
        if (!bundleResult.IsSuccess)
        {
            return WrapperValueResult<PassInspectionData>.Failure(bundleResult.Error!);
        }
        if (bundleResult.Value is not RangeBundle bundle)
        {
            return WrapperValueResult<PassInspectionData>.Failure(
                WrapperSupport.InternalError(
                    "The range bundle returned an unexpected value contract."));
        }

        foreach (var (scope, family) in new[]
                 {
                     (bundle.Metrics.Scope, "trace.range-metrics"),
                     (bundle.Shaders.Scope, "trace.range-shaders"),
                 })
        {
            if (!WrapperSupport.SameEventKey(eventFact.Key, scope))
            {
                return WrapperValueResult<PassInspectionData>.Failure(
                    WrapperSupport.InternalError(
                        $"{family} returned a different exact scope."));
            }
        }

        var metricsPage = bundle.Metrics.Metrics;
        var shadersPage = bundle.Shaders.Shaders;
        var instructionPage = bundle.InstructionMix?.Instructions;
        if (bundle.InstructionMix is not null &&
            !WrapperSupport.SameEventKey(eventFact.Key, bundle.InstructionMix.Scope))
        {
            return WrapperValueResult<PassInspectionData>.Failure(
                WrapperSupport.InternalError(
                    "trace.range-instruction-mix returned a different exact scope."));
        }
        if ((bundle.InstructionMix is null) == (bundle.InstructionMixError is null))
        {
            return WrapperValueResult<PassInspectionData>.Failure(
                WrapperSupport.InternalError(
                    "The range bundle returned an invalid Instruction Mix availability state."));
        }
        if (metricsPage.Truncated || shadersPage.Truncated ||
            instructionPage?.Truncated == true)
        {
            return WrapperValueResult<PassInspectionData>.Failure(
                WrapperSupport.InternalError(
                    "The range bundle did not return complete fact pages."));
        }
        context.AddRetrievedFacts(
            metricsPage.ReturnedCount +
            shadersPage.ReturnedCount +
            (instructionPage?.ReturnedCount ?? 0));

        return WrapperValueResult<PassInspectionData>.Success(
            new(
                eventFact,
                bundle.Metrics.TableCount,
                bundle.Metrics.RowCount,
                metricsPage.Items,
                shadersPage.Items,
                instructionPage?.Items,
                bundle.InstructionMixError,
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
            ProjectInstructionMix(data.InstructionMix, data.InstructionMixError),
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
