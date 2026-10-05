using System.Globalization;
using System.Text.RegularExpressions;
using NsightAnalyzer.Contracts;

namespace NsightAnalyzer.Wrappers;

internal static class CompareRangesWrapper
{
    private static readonly Regex DurationPattern = new(
        @"^\s*(?<bound><)?\s*(?<value>\d+(?:\.\d+)?)\s*(?<unit>ns|us|µs|μs|ms|s)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static async Task<OperationResult> ExecuteAsync(
        string targetTracePath,
        string baselineTracePath,
        string? viewerPath,
        int timeoutMs,
        int targetEventOrdinal,
        int baselineEventOrdinal,
        IReadOnlyList<string> metricTables,
        int topShaderCount,
        int metricCursor,
        int metricLimit,
        RangeSections sections,
        bool includeUnchanged = false)
    {
        var deadline = new WrapperDeadline(timeoutMs);
        var targetResult = await InspectPassWrapper.InspectCoreAsync(
            targetTracePath,
            viewerPath,
            targetEventOrdinal,
            metricTables,
            sections,
            deadline: deadline);
        if (!targetResult.IsSuccess)
        {
            return OperationResult.Failure(targetResult.Error!);
        }
        var target = targetResult.Value!;
        if (sections.HasFlag(RangeSections.InstructionMix) && target.InstructionMix is null)
        {
            return OperationResult.Failure(target.InstructionMixError!);
        }

        var baselineResult = await InspectPassWrapper.InspectCoreAsync(
            baselineTracePath,
            viewerPath,
            baselineEventOrdinal,
            metricTables,
            sections,
            deadline: deadline);
        if (!baselineResult.IsSuccess)
        {
            return OperationResult.Failure(baselineResult.Error!);
        }
        var baseline = baselineResult.Value!;
        if (sections.HasFlag(RangeSections.InstructionMix) && baseline.InstructionMix is null)
        {
            return OperationResult.Failure(baseline.InstructionMixError!);
        }

        if (target.Context.SourceIdentity is null ||
            baseline.Context.SourceIdentity is null)
        {
            return OperationResult.Failure(
                WrapperSupport.InternalError(
                    "A completed pass inspection has no source identity."));
        }
        if (!WrapperSupport.SameDecoder(
                target.Context.SourceIdentity,
                baseline.Context.SourceIdentity))
        {
            return OperationResult.Failure(
                ErrorCategory.AdapterMismatch,
                "wrapper.decoder_mismatch",
                "Target and baseline were not decoded by the exact same pinned Viewer source.");
        }

        return Project(target, baseline, targetTracePath, baselineTracePath,
            topShaderCount, metricCursor, metricLimit, sections, deadline, includeUnchanged);
    }

    internal static OperationResult Project(
        PassInspectionData target, PassInspectionData baseline,
        string targetTracePath, string baselineTracePath,
        int topShaderCount, int metricCursor, int metricLimit,
        RangeSections sections, WrapperDeadline deadline, bool includeUnchanged = false)
    {
        var metrics = sections.HasFlag(RangeSections.Metrics)
            ? CompareMetrics(target.Metrics, baseline.Metrics, metricCursor, metricLimit, includeUnchanged) : null;
        if (metrics is { IsSuccess: false })
        {
            return OperationResult.Failure(metrics.Error!);
        }
        var shaders = sections.HasFlag(RangeSections.Shaders)
            ? CompareShaders(target.Shaders, baseline.Shaders, topShaderCount) : null;
        if (shaders is { IsSuccess: false })
        {
            return OperationResult.Failure(shaders.Error!);
        }
        var instructions = sections.HasFlag(RangeSections.InstructionMix)
            ? CompareInstructions(target.InstructionMix!, baseline.InstructionMix!) : null;
        if (instructions is { IsSuccess: false })
        {
            return OperationResult.Failure(instructions.Error!);
        }
        var warnings = WrapperSupport.MergeWarnings(target.Context.Warnings, baseline.Context.Warnings).ToList();
        if (sections.HasFlag(RangeSections.Shaders))
        {
            warnings.Add(WrapperSupport.ShaderSampleWarning);
        }
        if (target.Event.Key.Description != baseline.Event.Key.Description)
        {
            warnings.Add(new(
                "wrapper.range_names_differ",
                "Target and baseline have different exact event descriptions; comparison is mechanical only."));
        }
        if (target.Event.Depth != baseline.Event.Depth)
        {
            warnings.Add(new(
                "wrapper.range_depths_differ",
                "Target and baseline have different event-tree depths; verify equivalent timing granularity."));
        }
        var value = new CompareRangesValue(
            new(
                Path.GetFileName(Path.GetFullPath(targetTracePath)),
                target.Context.ArtifactIdentity!,
                target.Event),
            new(
                Path.GetFileName(Path.GetFullPath(baselineTracePath)),
                baseline.Context.ArtifactIdentity!,
                baseline.Event),
            CompareDurations(target.Event, baseline.Event),
            RangeSectionNames.For(sections),
            metrics?.Value,
            shaders?.Value,
            instructions?.Value,
            sections.HasFlag(RangeSections.InstructionMix)
                ? CompareStalls(target.InstructionMix!, baseline.InstructionMix!) : null,
            new(
                checked(target.Context.AtomCallCount + baseline.Context.AtomCallCount),
                checked(target.Context.RetrievedFactCount +
                    baseline.Context.RetrievedFactCount),
                2,
                deadline.ElapsedMilliseconds));
        return OperationResult.Success(
            value,
            WrapperSupport.MergeProvenance(
                target.Context.Provenance,
                baseline.Context.Provenance),
            warnings);
    }

    private static WrapperValueResult<RangeMetricComparison> CompareMetrics(
        IReadOnlyList<RangeMetricFact> targetFacts,
        IReadOnlyList<RangeMetricFact> baselineFacts,
        int cursor,
        int limit,
        bool includeUnchanged)
    {
        var target = BuildUniqueDictionary(targetFacts, MetricIdentity);
        if (!target.IsSuccess)
        {
            return WrapperValueResult<RangeMetricComparison>.Failure(target.Error!);
        }
        var baseline = BuildUniqueDictionary(baselineFacts, MetricIdentity);
        if (!baseline.IsSuccess)
        {
            return WrapperValueResult<RangeMetricComparison>.Failure(baseline.Error!);
        }

        var keys = target.Value!.Keys
            .Union(baseline.Value!.Keys)
            .OrderBy(key => key.TableName, StringComparer.Ordinal)
            .ThenBy(key => key.TableOccurrence)
            .ThenBy(key => key.RowName, StringComparer.Ordinal)
            .ThenBy(key => key.RowOccurrence)
            .ThenBy(key => key.ColumnName, StringComparer.Ordinal)
            .ThenBy(key => key.ColumnOccurrence)
            .ToArray();
        var items = new List<RangeMetricDeltaFact>(keys.Length);
        var matched = 0;
        var targetOnly = 0;
        var baselineOnly = 0;
        var numericComparable = 0;
        var changed = 0;

        foreach (var key in keys)
        {
            target.Value.TryGetValue(key, out var targetFact);
            baseline.Value.TryGetValue(key, out var baselineFact);
            var joinState = JoinState(targetFact is not null, baselineFact is not null);
            matched += joinState == "matched" ? 1 : 0;
            targetOnly += joinState == "targetOnly" ? 1 : 0;
            baselineOnly += joinState == "baselineOnly" ? 1 : 0;

            var targetSide = targetFact is null ? null : MetricSide(targetFact);
            var baselineSide = baselineFact is null ? null : MetricSide(baselineFact);
            var comparable = targetSide?.NumericValue is not null &&
                baselineSide?.NumericValue is not null;
            decimal? delta = comparable
                ? targetSide!.NumericValue!.Value - baselineSide!.NumericValue!.Value
                : null;
            if (comparable)
            {
                numericComparable++;
            }

            var itemChanged = joinState != "matched" ||
                comparable && (delta != 0m || !MetricEvidenceEquals(targetSide!, baselineSide!)) ||
                !comparable && !MetricValueEquals(targetFact!, baselineFact!);
            if (itemChanged)
            {
                changed++;
            }
            items.Add(new(
                key,
                joinState,
                targetFact?.Description ?? baselineFact?.Description,
                targetFact is not null && baselineFact is not null &&
                    targetFact.Description != baselineFact.Description
                        ? baselineFact.Description
                        : null,
                targetSide,
                baselineSide,
                delta,
                delta is null
                    ? null
                    : RelativeDelta(delta.Value, baselineSide!.NumericValue!.Value),
                itemChanged));
        }

        var tableSummaries = items
            .GroupBy(item => (
                item.Identity.TableName,
                item.Identity.TableOccurrence))
            .OrderBy(group => group.Key.TableName, StringComparer.Ordinal)
            .ThenBy(group => group.Key.TableOccurrence)
            .Select(group => new RangeMetricTableComparisonFact(
                group.Key.TableName,
                group.Key.TableOccurrence,
                group.Count(),
                group.Count(item => item.JoinState == "matched"),
                group.Count(item => item.JoinState == "targetOnly"),
                group.Count(item => item.JoinState == "baselineOnly"),
                group.Count(item => item.Target?.NumericValue is not null &&
                    item.Baseline?.NumericValue is not null),
                group.Count(item => item.Changed)))
            .ToArray();
        var selected = items.Where(item => includeUnchanged || item.Changed).ToArray();
        return WrapperValueResult<RangeMetricComparison>.Success(
            new(
                "targetMinusBaseline",
                includeUnchanged ? "all" : "changed",
                items.Count,
                matched,
                targetOnly,
                baselineOnly,
                numericComparable,
                changed,
                tableSummaries,
                WrapperSupport.Page(selected, cursor, limit)));
    }

    private static WrapperValueResult<RangeShaderComparison> CompareShaders(
        IReadOnlyList<RangeShaderFact> targetFacts,
        IReadOnlyList<RangeShaderFact> baselineFacts,
        int topShaderCount)
    {
        var target = BuildShaderDictionary(targetFacts);
        if (!target.IsSuccess)
        {
            return WrapperValueResult<RangeShaderComparison>.Failure(target.Error!);
        }
        var baseline = BuildShaderDictionary(baselineFacts);
        if (!baseline.IsSuccess)
        {
            return WrapperValueResult<RangeShaderComparison>.Failure(baseline.Error!);
        }

        var items = new List<RangeShaderDeltaFact>();
        foreach (var key in target.Value!.Keys.Union(baseline.Value!.Keys))
        {
            target.Value.TryGetValue(key, out var targetFact);
            baseline.Value.TryGetValue(key, out var baselineFact);
            var joinState = JoinState(targetFact is not null, baselineFact is not null);
            long? delta = joinState == "matched"
                ? targetFact!.SampleCount - baselineFact!.SampleCount
                : null;
            var instructionMixChanged = joinState == "matched" &&
                !targetFact!.InstructionMix.SequenceEqual(baselineFact!.InstructionMix);
            var topStallsChanged = joinState == "matched" &&
                !targetFact!.TopStalls.SequenceEqual(baselineFact!.TopStalls);
            var correlationChanged = joinState == "matched" &&
                targetFact!.Correlation != baselineFact!.Correlation;
            var itemChanged = joinState != "matched" ||
                !ShaderValueEquals(targetFact!, baselineFact!);
            items.Add(new(
                key,
                joinState,
                targetFact is null ? null : ShaderSide(targetFact),
                baselineFact is null ? null : ShaderSide(baselineFact),
                delta,
                NullableDelta(
                    targetFact?.MaximumTheoreticalWarps,
                    baselineFact?.MaximumTheoreticalWarps,
                    joinState),
                NullableDelta(
                    targetFact?.StaticRegisters,
                    baselineFact?.StaticRegisters,
                    joinState),
                NullableDelta(
                    targetFact?.LiveRegisters,
                    baselineFact?.LiveRegisters,
                    joinState),
                NullableDelta(
                    targetFact?.StaticInstructionCount,
                    baselineFact?.StaticInstructionCount,
                    joinState),
                joinState == "matched"
                    ? targetFact!.DependencyAttributedSampleCount -
                        baselineFact!.DependencyAttributedSampleCount
                    : null,
                delta is null
                    ? null
                    : RelativeDelta(delta.Value, baselineFact!.SampleCount),
                instructionMixChanged,
                topStallsChanged,
                correlationChanged,
                itemChanged));
        }

        var ordered = items
            .OrderByDescending(item => item.Changed)
            .ThenByDescending(item => Math.Abs((decimal)(item.SampleCountDelta ?? 0L)))
            .ThenByDescending(item => Math.Max(
                item.Target?.SampleCount ?? 0L,
                item.Baseline?.SampleCount ?? 0L))
            .ThenBy(item => item.Identity.Stage, StringComparer.Ordinal)
            .ThenBy(item => item.Identity.Hash, StringComparer.Ordinal)
            .ThenBy(item => item.Identity.Name, StringComparer.Ordinal)
            .ThenBy(item => item.Identity.Pipeline, StringComparer.Ordinal)
            .ToArray();
        var returned = ordered.Take(topShaderCount).ToArray();
        return WrapperValueResult<RangeShaderComparison>.Success(
            new(
                "targetMinusBaseline",
                "changedThenAbsoluteSampleCountDeltaThenStableShaderIdentity",
                ordered.Length,
                ordered.Count(item => item.JoinState == "matched"),
                ordered.Count(item => item.JoinState == "targetOnly"),
                ordered.Count(item => item.JoinState == "baselineOnly"),
                ordered.Count(item => item.Changed),
                targetFacts.Sum(item => item.SampleCount),
                baselineFacts.Sum(item => item.SampleCount),
                returned.Length,
                returned.Length < ordered.Length,
                returned));
    }

    private static WrapperValueResult<RangeInstructionComparison> CompareInstructions(
        IReadOnlyList<RangeInstructionMixFact> targetFacts,
        IReadOnlyList<RangeInstructionMixFact> baselineFacts)
    {
        var target = BuildInstructionDictionary(targetFacts);
        if (!target.IsSuccess)
        {
            return WrapperValueResult<RangeInstructionComparison>.Failure(target.Error!);
        }
        var baseline = BuildInstructionDictionary(baselineFacts);
        if (!baseline.IsSuccess)
        {
            return WrapperValueResult<RangeInstructionComparison>.Failure(baseline.Error!);
        }

        var items = new List<RangeInstructionDeltaFact>();
        foreach (var key in target.Value!.Keys.Union(baseline.Value!.Keys))
        {
            target.Value.TryGetValue(key, out var targetFact);
            baseline.Value.TryGetValue(key, out var baselineFact);
            var joinState = JoinState(targetFact is not null, baselineFact is not null);
            long? sampleDelta = joinState == "matched"
                ? targetFact!.SampleCount - baselineFact!.SampleCount
                : null;
            long? instructionDelta = joinState == "matched"
                ? targetFact!.InstructionCount - baselineFact!.InstructionCount
                : null;
            var itemChanged = joinState != "matched" ||
                sampleDelta != 0L || instructionDelta != 0L;
            items.Add(new(
                key,
                joinState,
                targetFact is null ? null : new(
                    targetFact.SourceOrdinal,
                    targetFact.SampleCount,
                    targetFact.InstructionCount),
                baselineFact is null ? null : new(
                    baselineFact.SourceOrdinal,
                    baselineFact.SampleCount,
                    baselineFact.InstructionCount),
                sampleDelta,
                instructionDelta,
                sampleDelta is null
                    ? null
                    : RelativeDelta(sampleDelta.Value, baselineFact!.SampleCount),
                instructionDelta is null
                    ? null
                    : RelativeDelta(
                        instructionDelta.Value,
                        baselineFact!.InstructionCount),
                itemChanged));
        }

        var ordered = items
            .OrderByDescending(item => Math.Abs((decimal)(item.SampleCountDelta ?? 0L)))
            .ThenByDescending(item => Math.Abs((decimal)(item.InstructionCountDelta ?? 0L)))
            .ThenBy(item => item.Identity.Pipe, StringComparer.Ordinal)
            .ThenBy(item => item.Identity.Family, StringComparer.Ordinal)
            .ThenBy(item => item.Identity.Operation, StringComparer.Ordinal)
            .ThenBy(item => item.Identity.Occurrence)
            .ToArray();
        return WrapperValueResult<RangeInstructionComparison>.Success(
            new(
                "targetMinusBaseline",
                "absoluteSampleCountDeltaThenInstructionCountDeltaThenStableIdentity",
                ordered.Length,
                ordered.Count(item => item.JoinState == "matched"),
                ordered.Count(item => item.JoinState == "targetOnly"),
                ordered.Count(item => item.JoinState == "baselineOnly"),
                ordered.Count(item => item.Changed),
                ordered));
    }

    private static SummedStallComparison CompareStalls(
        IReadOnlyList<RangeInstructionMixFact> targetFacts,
        IReadOnlyList<RangeInstructionMixFact> baselineFacts)
    {
        var target = SumStalls(targetFacts);
        var baseline = SumStalls(baselineFacts);
        var items = target.Keys.Union(baseline.Keys)
            .Select(reason =>
            {
                var hasTarget = target.TryGetValue(reason, out var targetValue);
                var hasBaseline = baseline.TryGetValue(reason, out var baselineValue);
                long? delta = hasTarget && hasBaseline
                    ? targetValue.Sum - baselineValue.Sum
                    : null;
                return new SummedStallComparisonFact(
                    reason,
                    hasTarget ? targetValue.Sum : null,
                    hasTarget ? targetValue.Count : 0,
                    hasBaseline ? baselineValue.Sum : null,
                    hasBaseline ? baselineValue.Count : 0,
                    delta,
                    delta is null
                        ? null
                        : RelativeDelta(delta.Value, baselineValue.Sum));
            })
            .OrderByDescending(item =>
                Math.Abs((decimal)(item.SummedSampleCountDelta ?? 0L)))
            .ThenBy(item => item.Reason, StringComparer.Ordinal)
            .ToArray();
        return new(
            "sumOfStallSampleCountsAcrossCompleteInstructionMixRowsByExactReason",
            "targetMinusBaseline",
            "absoluteSummedSampleCountDeltaThenReason",
            items);
    }

    private static IReadOnlyList<DurationComparisonFact> CompareDurations(
        EventFact target,
        EventFact baseline) =>
        [
            CompareDuration("duration", target.Duration, baseline.Duration),
            CompareDuration("gpuDuration", target.GpuDuration, baseline.GpuDuration),
            CompareDuration("cpuDuration", target.CpuDuration, baseline.CpuDuration),
        ];

    private static DurationComparisonFact CompareDuration(
        string field,
        string? targetDisplay,
        string? baselineDisplay)
    {
        var target = ParseDuration(targetDisplay);
        var baseline = ParseDuration(baselineDisplay);
        decimal? delta = target.NumericState == "parsedDisplay" &&
            baseline.NumericState == "parsedDisplay"
                ? target.Milliseconds!.Value - baseline.Milliseconds!.Value
                : null;
        return new(
            field,
            "viewerDisplay",
            target,
            baseline,
            delta,
            delta is null
                ? null
                : RelativeDelta(delta.Value, baseline.Milliseconds!.Value));
    }

    private static DisplayDurationValue ParseDuration(string? display)
    {
        if (string.IsNullOrWhiteSpace(display))
        {
            return new(display, null, "absent");
        }
        var match = DurationPattern.Match(display);
        if (!match.Success || !decimal.TryParse(
                match.Groups["value"].Value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var value))
        {
            return new(display, null, "unparsedDisplay");
        }

        var milliseconds = match.Groups["unit"].Value.ToLowerInvariant() switch
        {
            "ns" => value / 1_000_000m,
            "us" or "µs" or "μs" => value / 1_000m,
            "ms" => value,
            "s" => value * 1_000m,
            _ => throw new InvalidOperationException("Unreachable duration unit."),
        };
        return new(
            display,
            milliseconds,
            match.Groups["bound"].Success
                ? "upperBoundDisplay"
                : "parsedDisplay");
    }

    private static RangeMetricComparisonIdentity MetricIdentity(RangeMetricFact fact) =>
        new(
            fact.TableName,
            fact.TableOccurrence,
            fact.RowName,
            fact.RowOccurrence,
            fact.ColumnName,
            fact.ColumnOccurrence,
            fact.Unit);

    private static RangeShaderComparisonIdentity ShaderIdentity(RangeShaderFact fact) =>
        new(
            fact.Key.Stage,
            fact.Key.Name,
            fact.Key.Hash,
            fact.Key.Pipeline);

    private static RangeMetricComparisonSide MetricSide(RangeMetricFact fact)
    {
        var numeric = MetricNumericValue(fact);
        var source = numeric.Source ?? (fact.PreciseValue is not null ? "preciseValue" : "displayValue");
        var value = source == "preciseValue" ? fact.PreciseValue : fact.DisplayValue;
        return new(
            fact.SourceOrdinal,
            value,
            source,
            SafeValueKind(value),
            fact.Availability,
            numeric.Value,
            numeric.State,
            numeric.Source,
            numeric.Resolution);
    }

    private static RangeShaderComparisonSide ShaderSide(RangeShaderFact fact) =>
        new(
            fact.Key,
            fact.SampleCount,
            fact.MaximumTheoreticalWarps,
            fact.StaticRegisters,
            fact.SharedMemory,
            fact.CtaDimensions,
            fact.LiveRegisters,
            fact.StaticInstructionCount,
            fact.DependencyAttributedSampleCount,
            fact.AverageWarpLatency,
            fact.Correlation,
            fact.SampleAvailability,
            fact.DependencySampleAvailability);

    private static (decimal? Value, string State, string? Source, decimal? Resolution) MetricNumericValue(
        RangeMetricFact fact)
    {
        if (fact.Availability != "available")
        {
            return (null, fact.Availability, null, null);
        }
        var precise = ParseMetricNumber(fact.PreciseValue, fact.Unit);
        if (precise.Value is not null)
        {
            return (precise.Value, precise.State == "parsed" ? "parsedPrecise" : precise.State,
                "preciseValue", precise.Resolution);
        }
        var display = ParseMetricNumber(fact.DisplayValue, fact.Unit);
        // A bound is evidence of uncertainty even if another display can be parsed.
        if (precise.State == "bounded" || display.State == "bounded")
        {
            return (null, "bounded", null, null);
        }
        if (display.Value is not null)
        {
            return (display.Value, display.State == "parsed" ? "parsedDisplay" : display.State,
                "displayValue", display.Resolution);
        }
        return (null, precise.State == "absent" && display.State == "absent" ? "absent" : "notNumeric", null, null);
    }

    private static (decimal? Value, string State, decimal? Resolution) ParseMetricNumber(
        object? raw,
        string? unit)
    {
        if (raw is null)
        {
            return (null, "absent", null);
        }
        try
        {
            if (raw is byte or sbyte or short or ushort or int or uint or long or ulong or
                float or double or decimal)
            {
                return (Convert.ToDecimal(raw, CultureInfo.InvariantCulture), "exact", null);
            }
        }
        catch (Exception exception) when (
            exception is FormatException or InvalidCastException or OverflowException)
        {
            return (null, "notNumeric", null);
        }
        if (raw is not string text)
        {
            return (null, "notNumeric", null);
        }

        var normalized = text.Trim().Replace("\u00a0", string.Empty, StringComparison.Ordinal);
        if (normalized.StartsWith('<') || normalized.StartsWith('>'))
        {
            return (null, "bounded", null);
        }
        if (!string.IsNullOrWhiteSpace(unit) &&
            normalized.EndsWith(unit, StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^unit.Length].Trim();
        }
        if (normalized.EndsWith('%'))
        {
            normalized = normalized[..^1].Trim();
        }
        return decimal.TryParse(
            normalized,
            NumberStyles.Float | NumberStyles.AllowThousands,
            CultureInfo.InvariantCulture,
            out var value)
            ? (value, "parsed", TextNumericResolution(normalized))
            : (null, "notNumeric", null);
    }

    private static decimal? TextNumericResolution(string text)
    {
        var exponentIndex = text.IndexOfAny(['e', 'E']);
        var mantissa = exponentIndex < 0 ? text : text[..exponentIndex];
        var exponent = exponentIndex < 0 ? 0 : int.Parse(text[(exponentIndex + 1)..], CultureInfo.InvariantCulture);
        var point = mantissa.IndexOf('.');
        var power = exponent - (point < 0 ? 0 : mantissa.Length - point - 1);
        if (power is < -28 or > 28)
        {
            return null;
        }
        var resolution = 1m;
        for (var index = 0; index < Math.Abs(power); index++)
        {
            resolution = power < 0 ? resolution / 10m : resolution * 10m;
        }
        return resolution;
    }

    private static bool MetricEvidenceEquals(RangeMetricComparisonSide target, RangeMetricComparisonSide baseline) =>
        target.Availability == baseline.Availability &&
        target.NumericState == baseline.NumericState &&
        target.NumericSource == baseline.NumericSource &&
        target.NumericResolution == baseline.NumericResolution &&
        target.ValueKind == baseline.ValueKind;

    private static WrapperValueResult<Dictionary<RangeShaderComparisonIdentity, RangeShaderFact>> BuildShaderDictionary(
        IReadOnlyList<RangeShaderFact> facts)
    {
        var result = new Dictionary<RangeShaderComparisonIdentity, RangeShaderFact>();
        foreach (var fact in facts)
        {
            if (!result.TryAdd(ShaderIdentity(fact), fact))
            {
                return WrapperValueResult<Dictionary<RangeShaderComparisonIdentity, RangeShaderFact>>.Failure(
                    new(ErrorCategory.Unavailable, "wrapper.shader_comparison_ambiguous",
                        "Repeated shaders have the same stage, hash, name and pipeline; their range-local occurrences cannot establish a cross-range pairing."));
            }
        }
        return WrapperValueResult<Dictionary<RangeShaderComparisonIdentity, RangeShaderFact>>.Success(result);
    }

    private static bool MetricValueEquals(
        RangeMetricFact target,
        RangeMetricFact baseline) =>
        target.Availability == baseline.Availability &&
        target.ValueKind == baseline.ValueKind &&
        target.Description == baseline.Description &&
        SafeValueEquals(target.DisplayValue, baseline.DisplayValue) &&
        SafeValueEquals(target.PreciseValue, baseline.PreciseValue);

    private static bool ShaderValueEquals(
        RangeShaderFact target,
        RangeShaderFact baseline) =>
        target.SampleCount == baseline.SampleCount &&
        target.SampleAvailability == baseline.SampleAvailability &&
        target.DependencySampleAvailability == baseline.DependencySampleAvailability &&
        target.MaximumTheoreticalWarps == baseline.MaximumTheoreticalWarps &&
        target.StaticRegisters == baseline.StaticRegisters &&
        target.SharedMemory == baseline.SharedMemory &&
        target.CtaDimensions == baseline.CtaDimensions &&
        target.LiveRegisters == baseline.LiveRegisters &&
        target.StaticInstructionCount == baseline.StaticInstructionCount &&
        target.DependencyAttributedSampleCount == baseline.DependencyAttributedSampleCount &&
        target.AverageWarpLatency == baseline.AverageWarpLatency &&
        target.Correlation == baseline.Correlation &&
        target.InstructionMix.SequenceEqual(baseline.InstructionMix) &&
        target.TopStalls.SequenceEqual(baseline.TopStalls);

    private static WrapperValueResult<Dictionary<TKey, TValue>> BuildUniqueDictionary<TValue, TKey>(
        IReadOnlyList<TValue> facts,
        Func<TValue, TKey> identity)
        where TValue : class
        where TKey : notnull
    {
        var result = new Dictionary<TKey, TValue>();
        foreach (var fact in facts)
        {
            if (!result.TryAdd(identity(fact), fact))
            {
                return WrapperValueResult<Dictionary<TKey, TValue>>.Failure(
                    new(
                        ErrorCategory.AdapterMismatch,
                        "wrapper.comparison_identity_duplicate",
                        "A supposedly stable comparison identity occurred more than once."));
            }
        }
        return WrapperValueResult<Dictionary<TKey, TValue>>.Success(result);
    }

    private static WrapperValueResult<Dictionary<RangeInstructionComparisonIdentity, RangeInstructionMixFact>>
        BuildInstructionDictionary(IReadOnlyList<RangeInstructionMixFact> facts)
    {
        var occurrences = new Dictionary<(string, string, string?), int>();
        var result = new Dictionary<RangeInstructionComparisonIdentity, RangeInstructionMixFact>();
        foreach (var fact in facts.OrderBy(item => item.SourceOrdinal))
        {
            var baseIdentity = (fact.Pipe, fact.Family, fact.Operation);
            var occurrence = occurrences.GetValueOrDefault(baseIdentity);
            occurrences[baseIdentity] = occurrence + 1;
            var identity = new RangeInstructionComparisonIdentity(
                fact.Pipe,
                fact.Family,
                fact.Operation,
                occurrence);
            if (!result.TryAdd(identity, fact))
            {
                return WrapperValueResult<Dictionary<RangeInstructionComparisonIdentity, RangeInstructionMixFact>>
                    .Failure(new(
                        ErrorCategory.AdapterMismatch,
                        "wrapper.comparison_identity_duplicate",
                        "A supposedly stable instruction comparison identity occurred more than once."));
            }
        }
        return WrapperValueResult<Dictionary<RangeInstructionComparisonIdentity, RangeInstructionMixFact>>
            .Success(result);
    }

    private static Dictionary<string, (long Sum, int Count)> SumStalls(
        IReadOnlyList<RangeInstructionMixFact> facts)
    {
        var result = new Dictionary<string, (long Sum, int Count)>(StringComparer.Ordinal);
        foreach (var stall in facts.SelectMany(fact => fact.Stalls))
        {
            var current = result.GetValueOrDefault(stall.Reason);
            result[stall.Reason] = (
                checked(current.Sum + stall.SampleCount),
                checked(current.Count + 1));
        }
        return result;
    }

    private static string JoinState(bool hasTarget, bool hasBaseline) =>
        hasTarget && hasBaseline
            ? "matched"
            : hasTarget ? "targetOnly" : "baselineOnly";

    private static decimal? RelativeDelta(decimal delta, decimal baseline) =>
        baseline == 0m
            ? null
            : Math.Round(
                delta * 100m / Math.Abs(baseline),
                9,
                MidpointRounding.AwayFromZero);

    private static decimal? RelativeDelta(long delta, long baseline) =>
        RelativeDelta((decimal)delta, baseline);

    private static bool SafeValueEquals(object? left, object? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }
        if (left is object?[] leftItems && right is object?[] rightItems)
        {
            return leftItems.Length == rightItems.Length &&
                leftItems.Zip(rightItems).All(pair =>
                    SafeValueEquals(pair.First, pair.Second));
        }
        return Equals(left, right);
    }

    private static int? NullableDelta(int? target, int? baseline, string joinState) =>
        joinState == "matched" && target is not null && baseline is not null
            ? target.Value - baseline.Value
            : null;

    private static string SafeValueKind(object? value) => value switch
    {
        null => "null",
        bool => "boolean",
        byte or sbyte or short or ushort or int or uint or long or ulong => "integer",
        float or double or decimal => "number",
        string => "string",
        Array => "list",
        _ => "unknown",
    };
}
