using System.Globalization;
using System.Text.RegularExpressions;
using NsightAnalyzer.Contracts;
using NsightAnalyzer.Operations;

namespace NsightAnalyzer.Wrappers;

internal static class CompareFrameTimingWrapper
{
    private static readonly Regex TimePattern = new(
        @"^\s*(?<bound><)?\s*(?<value>\d+(?:\.\d+)?)\s*(?<unit>ns|us|µs|μs|ms|s)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static async Task<OperationResult> ExecuteAsync(
        string tracePath,
        string? viewerPath,
        int timeoutMs,
        int targetFrameEventOrdinal,
        int targetFrameIndex,
        int baselineFrameEventOrdinal,
        int baselineFrameIndex,
        int analysisSeedEventOrdinal,
        int presentQueueEventOrdinal)
    {
        var deadline = new WrapperDeadline(timeoutMs);
        var context = new WrapperContext(deadline);

        var targetResult = await GetExactEventAsync(
            context, tracePath, viewerPath, targetFrameEventOrdinal);
        if (!targetResult.IsSuccess)
        {
            return OperationResult.Failure(targetResult.Error!);
        }

        var baselineResult = await GetExactEventAsync(
            context, tracePath, viewerPath, baselineFrameEventOrdinal);
        if (!baselineResult.IsSuccess)
        {
            return OperationResult.Failure(baselineResult.Error!);
        }

        var targetEvent = targetResult.Value!;
        var baselineEvent = baselineResult.Value!;
        var targetIdentity = EventIdentity(targetEvent.Key.Description);
        var baselineIdentity = EventIdentity(baselineEvent.Key.Description);
        var targetParentPath = targetEvent.Key.TreePath.SkipLast(1).ToArray();
        var baselineParentPath = baselineEvent.Key.TreePath.SkipLast(1).ToArray();
        if (targetEvent.Depth != baselineEvent.Depth ||
            !targetIdentity.Equals(baselineIdentity, StringComparison.Ordinal) ||
            !targetParentPath.SequenceEqual(baselineParentPath))
        {
            return OperationResult.Failure(
                ErrorCategory.InvalidInput,
                "wrapper.frame_event_grain_mismatch",
                "Target and baseline frame events must have the same parent, depth, and semantic event identity.",
                $"targetDepth={targetEvent.Depth}; baselineDepth={baselineEvent.Depth}; " +
                $"targetIdentity={targetIdentity}; baselineIdentity={baselineIdentity}");
        }

        var presentResult = await CollectPresentSequenceAsync(
            context, tracePath, viewerPath, presentQueueEventOrdinal);
        if (!presentResult.IsSuccess)
        {
            return OperationResult.Failure(presentResult.Error!);
        }

        var analysisResult = await CollectAnalysisFramesAsync(
            context, tracePath, viewerPath, analysisSeedEventOrdinal);
        if (!analysisResult.IsSuccess)
        {
            return OperationResult.Failure(analysisResult.Error!);
        }

        var presentSequence = presentResult.Value!;
        var analysisFrames = analysisResult.Value!;
        var sequenceError = ValidateFrameSequence(presentSequence, analysisFrames);
        if (sequenceError is not null)
        {
            return OperationResult.Failure(sequenceError);
        }

        var targetEndpointResult = BuildEndpoint(
            targetFrameIndex, targetEvent, presentSequence.Presents, analysisFrames.Items);
        if (!targetEndpointResult.IsSuccess)
        {
            return OperationResult.Failure(targetEndpointResult.Error!);
        }

        var baselineEndpointResult = BuildEndpoint(
            baselineFrameIndex, baselineEvent, presentSequence.Presents, analysisFrames.Items);
        if (!baselineEndpointResult.IsSuccess)
        {
            return OperationResult.Failure(baselineEndpointResult.Error!);
        }

        var target = targetEndpointResult.Value!;
        var baseline = baselineEndpointResult.Value!;
        var deltas = CompareDurations(target.Durations, baseline.Durations);
        var query = new CompareFrameTimingQuery(
            targetFrameIndex,
            targetFrameEventOrdinal,
            baselineFrameIndex,
            baselineFrameEventOrdinal,
            analysisSeedEventOrdinal,
            presentQueueEventOrdinal);
        var sequence = new FrameTimingSequenceFact(
            presentSequence.Queue,
            analysisFrames.SeedScope,
            "directChildrenOfExplicitPresentQueue",
            "Present",
            "traceAnalysisFrameIndexEqualsZeroBasedPresentOccurrence",
            presentSequence.Presents.Count,
            analysisFrames.TotalCount,
            targetIdentity,
            targetEvent.Depth,
            targetParentPath);
        var value = new CompareFrameTimingValue(
            query,
            sequence,
            target,
            baseline,
            "contractFieldOrder",
            deltas,
            context.Stats(checked(presentSequence.ScannedEventCount + 2)));
        return OperationResult.Success(value, context.Provenance, context.Warnings);
    }

    private static async Task<WrapperValueResult<EventFact>> GetExactEventAsync(
        WrapperContext context,
        string tracePath,
        string? viewerPath,
        int preorderOrdinal)
    {
        var atom = await context.InvokeAtomAsync(
            remainingMs => TraceEventsOperation.ExecuteAsync(
                tracePath, viewerPath, remainingMs, preorderOrdinal, 1));
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

        var pageError = WrapperSupport.ValidatePage(value.Events, preorderOrdinal, 1);
        if (pageError is not null)
        {
            return WrapperValueResult<EventFact>.Failure(pageError);
        }
        context.AddRetrievedFacts(value.Events.ReturnedCount);
        if (value.Events.ReturnedCount != 1)
        {
            return WrapperValueResult<EventFact>.Failure(
                new(
                    ErrorCategory.NotFound,
                    "wrapper.frame_event_not_found",
                    "The exact frame-event preorder ordinal was not found.",
                    $"preorderOrdinal={preorderOrdinal}"));
        }

        var fact = value.Events.Items[0];
        if (fact.Key.PreorderOrdinal != preorderOrdinal ||
            fact.Depth < 0 || fact.Key.TreePath.Count != fact.Depth + 1)
        {
            return WrapperValueResult<EventFact>.Failure(
                WrapperSupport.InternalError(
                    "trace.events did not return the exact requested hierarchy identity."));
        }
        return WrapperValueResult<EventFact>.Success(fact);
    }

    private static async Task<WrapperValueResult<PresentSequence>> CollectPresentSequenceAsync(
        WrapperContext context,
        string tracePath,
        string? viewerPath,
        int queueOrdinal)
    {
        var cursor = queueOrdinal;
        int? expectedTotal = null;
        EventFact? queue = null;
        var presents = new List<EventFact>();
        var directChildCount = 0;
        var scannedEventCount = 0;
        var complete = false;

        while (!complete)
        {
            var atom = await context.InvokeAtomAsync(
                remainingMs => TraceEventsOperation.ExecuteAsync(
                    tracePath,
                    viewerPath,
                    remainingMs,
                    cursor,
                    WrapperSupport.AtomPageLimit));
            if (!atom.IsSuccess)
            {
                return WrapperValueResult<PresentSequence>.Failure(atom.Error!);
            }
            if (atom.Value is not TraceEventsValue value)
            {
                return WrapperValueResult<PresentSequence>.Failure(
                    WrapperSupport.InternalError(
                        "trace.events returned an unexpected value contract."));
            }

            var page = value.Events;
            var pageError = WrapperSupport.ValidatePage(
                page, cursor, WrapperSupport.AtomPageLimit, expectedTotal);
            if (pageError is not null)
            {
                return WrapperValueResult<PresentSequence>.Failure(pageError);
            }
            expectedTotal ??= page.TotalCount;
            context.AddRetrievedFacts(page.ReturnedCount);

            for (var index = 0; index < page.Items.Count; index++)
            {
                var fact = page.Items[index];
                var expectedOrdinal = checked(page.Cursor + index);
                if (fact.Key.PreorderOrdinal != expectedOrdinal ||
                    fact.Depth < 0 || fact.Key.TreePath.Count != fact.Depth + 1)
                {
                    return WrapperValueResult<PresentSequence>.Failure(
                        WrapperSupport.InternalError(
                            "trace.events did not preserve contiguous preorder hierarchy identity."));
                }

                if (queue is null)
                {
                    if (expectedOrdinal != queueOrdinal)
                    {
                        return WrapperValueResult<PresentSequence>.Failure(
                            WrapperSupport.InternalError(
                                "trace.events did not return the requested Present queue first."));
                    }
                    queue = fact;
                    scannedEventCount++;
                    continue;
                }

                if (!WrapperSupport.IsStrictDescendant(
                        fact.Key.TreePath, queue.Key.TreePath))
                {
                    complete = true;
                    break;
                }

                scannedEventCount++;
                if (fact.Depth == queue.Depth + 1)
                {
                    directChildCount++;
                    if (EventIdentity(fact.Key.Description).Equals(
                            "Present", StringComparison.Ordinal))
                    {
                        presents.Add(fact);
                    }
                }
            }

            if (complete || page.NextCursor is null)
            {
                break;
            }
            cursor = page.NextCursor.Value;
        }

        if (queue is null)
        {
            return WrapperValueResult<PresentSequence>.Failure(
                new(
                    ErrorCategory.NotFound,
                    "wrapper.present_queue_not_found",
                    "The exact Present-queue preorder ordinal was not found.",
                    $"preorderOrdinal={queueOrdinal}"));
        }
        if (directChildCount != queue.ChildCount)
        {
            return WrapperValueResult<PresentSequence>.Failure(
                WrapperSupport.InternalError(
                    "The explicit Present queue subtree did not close to its direct child count."));
        }
        if (presents.Count == 0)
        {
            return WrapperValueResult<PresentSequence>.Failure(
                new(
                    ErrorCategory.NotFound,
                    "wrapper.present_events_not_found",
                    "The explicit queue has no direct Present command events."));
        }

        return WrapperValueResult<PresentSequence>.Success(
            new(queue, presents, scannedEventCount));
    }

    private static async Task<WrapperValueResult<AnalysisFrameSequence>> CollectAnalysisFramesAsync(
        WrapperContext context,
        string tracePath,
        string? viewerPath,
        int seedOrdinal)
    {
        EventKey? seedScope = null;
        var collected = await WrapperSupport.CollectPagesAsync<TraceAnalysisValue, TraceAnalysisRangeFact>(
            context,
            (cursor, limit, remainingMs) => TraceAnalysisOperation.ExecuteAsync(
                tracePath,
                viewerPath,
                remainingMs,
                seedOrdinal,
                null,
                cursor,
                limit),
            value => value.Ranges,
            value =>
            {
                if (value.SeedScope.PreorderOrdinal != seedOrdinal)
                {
                    return WrapperSupport.InternalError(
                        "trace.analysis did not retain the exact explicit seed EventKey.");
                }
                if (seedScope is not null &&
                    !WrapperSupport.SameEventKey(seedScope, value.SeedScope))
                {
                    return WrapperSupport.InternalError(
                        "trace.analysis seed identity changed across pages.");
                }
                seedScope ??= value.SeedScope;
                return null;
            });
        if (!collected.IsSuccess)
        {
            return WrapperValueResult<AnalysisFrameSequence>.Failure(collected.Error!);
        }
        if (seedScope is null)
        {
            return WrapperValueResult<AnalysisFrameSequence>.Failure(
                WrapperSupport.InternalError(
                    "trace.analysis returned no verified seed scope."));
        }
        return WrapperValueResult<AnalysisFrameSequence>.Success(
            new(seedScope, collected.Value!.Items, collected.Value.TotalCount));
    }

    private static OperationError? ValidateFrameSequence(
        PresentSequence presentSequence,
        AnalysisFrameSequence analysisFrames)
    {
        if (presentSequence.Presents.Count != analysisFrames.TotalCount)
        {
            return new(
                ErrorCategory.Unavailable,
                "wrapper.frame_sequence_count_mismatch",
                "Trace Analysis frames did not close one-to-one against direct Present occurrences.",
                $"analysisFrames={analysisFrames.TotalCount}; " +
                $"presentEvents={presentSequence.Presents.Count}");
        }

        var indexes = analysisFrames.Items
            .Select(item => item.FrameIndex)
            .ToArray();
        if (indexes.Any(index => index is null) ||
            indexes.Distinct().Count() != analysisFrames.TotalCount ||
            !indexes.OrderBy(index => index).SequenceEqual(
                Enumerable.Range(0, analysisFrames.TotalCount).Select(index => (int?)index)))
        {
            return new(
                ErrorCategory.Unavailable,
                "wrapper.frame_sequence_identity_unavailable",
                "Trace Analysis did not expose one contiguous, unique zero-based frame sequence.");
        }
        return null;
    }

    private static WrapperValueResult<FrameTimingEndpoint> BuildEndpoint(
        int frameIndex,
        EventFact frameEvent,
        IReadOnlyList<EventFact> presents,
        IReadOnlyList<TraceAnalysisRangeFact> analysisFrames)
    {
        if (frameIndex <= 0 || frameIndex >= presents.Count)
        {
            return WrapperValueResult<FrameTimingEndpoint>.Failure(
                new(
                    ErrorCategory.NotFound,
                    "wrapper.frame_index_not_found",
                    "The requested frame index has no preceding and current Present occurrence.",
                    $"frameIndex={frameIndex}; presentEventCount={presents.Count}"));
        }

        var matchingAnalysis = analysisFrames
            .Where(item => item.FrameIndex == frameIndex)
            .ToArray();
        if (matchingAnalysis.Length != 1)
        {
            return WrapperValueResult<FrameTimingEndpoint>.Failure(
                new(
                    ErrorCategory.NotFound,
                    "wrapper.analysis_frame_not_found",
                    "The requested frame index did not resolve to exactly one Trace Analysis row.",
                    $"frameIndex={frameIndex}; matches={matchingAnalysis.Length}"));
        }

        var analysis = matchingAnalysis[0];
        if (analysis.DurationMs is null)
        {
            return WrapperValueResult<FrameTimingEndpoint>.Failure(
                new(
                    ErrorCategory.Unavailable,
                    "wrapper.analysis_duration_unavailable",
                    "The selected Trace Analysis frame has no numeric duration.",
                    $"frameIndex={frameIndex}"));
        }

        var previousPresent = presents[frameIndex - 1];
        var present = presents[frameIndex];
        var previousPresentStartResult = ParseTimestamp(
            previousPresent.Start, "previous Present start");
        var frameStartResult = ParseTimestamp(frameEvent.Start, "selected frame-event start");
        var frameEndResult = ParseTimestamp(frameEvent.End, "selected frame-event end");
        var presentStartResult = ParseTimestamp(present.Start, "current Present start");
        var parseError = previousPresentStartResult.Error ??
            frameStartResult.Error ?? frameEndResult.Error ?? presentStartResult.Error;
        if (parseError is not null)
        {
            return WrapperValueResult<FrameTimingEndpoint>.Failure(parseError);
        }

        var previousPresentStart = previousPresentStartResult.Value!;
        var frameStart = frameStartResult.Value!;
        var frameEnd = frameEndResult.Value!;
        var presentStart = presentStartResult.Value!;
        if (frameStart.Milliseconds < previousPresentStart.Milliseconds ||
            frameEnd.Milliseconds < frameStart.Milliseconds ||
            presentStart.Milliseconds < frameEnd.Milliseconds)
        {
            return WrapperValueResult<FrameTimingEndpoint>.Failure(
                new(
                    ErrorCategory.Unavailable,
                    "wrapper.frame_timeline_order_mismatch",
                    "The selected frame event was not contained between its preceding and current Present starts.",
                    $"frameIndex={frameIndex}"));
        }

        var analysisResolution = DecimalResolution(analysis.DurationMs.Value);
        var durations = new List<FrameTimingDurationFact>
        {
            new(
                "traceAnalysisFrameDuration",
                "trace.analysis.durationMs",
                analysis.DurationMs.Value,
                analysisResolution,
                "viewerNumericDisplay"),
            Difference(
                "presentStartInterval",
                "present.startMinusPreviousPresent.start",
                presentStart,
                previousPresentStart),
            Difference(
                "previousPresentToSelectedFrameStart",
                "selectedFrameEvent.startMinusPreviousPresent.start",
                frameStart,
                previousPresentStart),
            Difference(
                "selectedFrameEventStartEndInterval",
                "selectedFrameEvent.endMinusSelectedFrameEvent.start",
                frameEnd,
                frameStart),
            Difference(
                "selectedFrameEndToPresent",
                "present.startMinusSelectedFrameEvent.end",
                presentStart,
                frameEnd),
        };

        var presentInterval = durations[1];
        var residual = analysis.DurationMs.Value - presentInterval.Milliseconds;
        var tolerance = analysisResolution +
            presentInterval.DisplayResolutionMilliseconds;
        if (Math.Abs(residual) > tolerance)
        {
            return WrapperValueResult<FrameTimingEndpoint>.Failure(
                new(
                    ErrorCategory.Unavailable,
                    "wrapper.frame_timing_alignment_unverified",
                    "Trace Analysis duration did not align with the consecutive Present-start interval within Viewer display precision.",
                    $"frameIndex={frameIndex}; residualMs={residual}; toleranceMs={tolerance}"));
        }

        var alignment = new FrameTimingAlignmentFact(
            "traceAnalysis.durationMsMinusConsecutivePresentStartInterval",
            analysis.DurationMs.Value,
            presentInterval.Milliseconds,
            residual,
            tolerance,
            residual == 0m ? "exact" : "withinViewerDisplayPrecision");
        return WrapperValueResult<FrameTimingEndpoint>.Success(
            new(
                frameIndex,
                analysis,
                frameEvent,
                previousPresent,
                present,
                previousPresentStart,
                frameStart,
                frameEnd,
                presentStart,
                durations,
                alignment));
    }

    private static WrapperValueResult<ViewerTimelineTimestamp> ParseTimestamp(
        string? display,
        string field)
    {
        if (string.IsNullOrWhiteSpace(display))
        {
            return TimestampUnavailable(field, display, "absent");
        }

        var match = TimePattern.Match(display);
        if (!match.Success || match.Groups["bound"].Success ||
            !decimal.TryParse(
                match.Groups["value"].Value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var value))
        {
            return TimestampUnavailable(field, display, "unparsedDisplay");
        }

        var scale = MillisecondScale(match.Groups["unit"].Value);
        var resolution = TextResolution(match.Groups["value"].Value) * scale;
        return WrapperValueResult<ViewerTimelineTimestamp>.Success(
            new(display, value * scale, resolution, "parsedViewerDisplay"));
    }

    private static WrapperValueResult<ViewerTimelineTimestamp> TimestampUnavailable(
        string field,
        string? display,
        string state) =>
        WrapperValueResult<ViewerTimelineTimestamp>.Failure(
            new(
                ErrorCategory.Unavailable,
                "wrapper.timeline_timestamp_unavailable",
                "A required Viewer timeline timestamp was absent or not numeric.",
                $"field={field}; state={state}; display={display ?? "<null>"}"));

    private static FrameTimingDurationFact Difference(
        string field,
        string basis,
        ViewerTimelineTimestamp end,
        ViewerTimelineTimestamp start) =>
        new(
            field,
            basis,
            end.Milliseconds - start.Milliseconds,
            Math.Max(
                end.DisplayResolutionMilliseconds,
                start.DisplayResolutionMilliseconds),
            "derivedFromViewerDisplay");

    private static IReadOnlyList<FrameTimingDeltaFact> CompareDurations(
        IReadOnlyList<FrameTimingDurationFact> target,
        IReadOnlyList<FrameTimingDurationFact> baseline)
    {
        if (target.Count != baseline.Count ||
            !target.Select(item => item.Field).SequenceEqual(
                baseline.Select(item => item.Field)))
        {
            throw new InvalidOperationException(
                "Frame timing duration identities did not close in contract order.");
        }

        return target.Zip(baseline)
            .Select(pair =>
            {
                var delta = pair.First.Milliseconds - pair.Second.Milliseconds;
                return new FrameTimingDeltaFact(
                    pair.First.Field,
                    "targetMinusBaseline",
                    pair.First.Milliseconds,
                    pair.Second.Milliseconds,
                    delta,
                    RelativeDelta(delta, pair.Second.Milliseconds),
                    Math.Max(
                        pair.First.DisplayResolutionMilliseconds,
                        pair.Second.DisplayResolutionMilliseconds));
            })
            .ToArray();
    }

    private static decimal MillisecondScale(string unit) =>
        unit.ToLowerInvariant() switch
        {
            "ns" => 0.000001m,
            "us" or "µs" or "μs" => 0.001m,
            "ms" => 1m,
            "s" => 1_000m,
            _ => throw new InvalidOperationException("Unreachable timeline unit."),
        };

    private static decimal TextResolution(string value)
    {
        var separator = value.IndexOf('.');
        return separator < 0
            ? 1m
            : PowerOfTenResolution(value.Length - separator - 1);
    }

    private static decimal DecimalResolution(decimal value)
    {
        var bits = decimal.GetBits(value);
        var scale = (bits[3] >> 16) & 0x7f;
        return PowerOfTenResolution(scale);
    }

    private static decimal PowerOfTenResolution(int scale)
    {
        var resolution = 1m;
        for (var index = 0; index < scale; index++)
        {
            resolution /= 10m;
        }
        return resolution;
    }

    private static decimal? RelativeDelta(decimal delta, decimal baseline) =>
        baseline == 0m
            ? null
            : Math.Round(
                delta * 100m / Math.Abs(baseline),
                9,
                MidpointRounding.AwayFromZero);

    private static string EventIdentity(string description)
    {
        var delimiter = description.IndexOf('(');
        return delimiter < 0 ? description : description[..delimiter];
    }

    private sealed record PresentSequence(
        EventFact Queue,
        IReadOnlyList<EventFact> Presents,
        int ScannedEventCount);

    private sealed record AnalysisFrameSequence(
        EventKey SeedScope,
        IReadOnlyList<TraceAnalysisRangeFact> Items,
        int TotalCount);
}
