using NsightAnalyzer.Contracts;

namespace NsightAnalyzer.Cli;

internal sealed record ParsedCommand(
    string Operation,
    bool Compact,
    string? TracePath,
    string? ViewerPath,
    int TimeoutMs,
    string IdentityMode,
    int Cursor,
    int Limit,
    int? EventOrdinal,
    string? EventPath,
    IReadOnlyList<string> MetricTables,
    string? ShaderHash,
    int? ShaderOccurrence,
    IReadOnlyList<string> CounterNames,
    IReadOnlyList<string> RangeNames,
    string? EventName,
    int? EventOccurrence,
    int? WithinEventOrdinal,
    string? BaselineTracePath,
    int? BaselineEventOrdinal,
    int? TargetFrameIndex,
    int? BaselineFrameIndex,
    int? AnalysisSeedEventOrdinal,
    int? PresentQueueEventOrdinal,
    int TopShaderCount);

internal sealed record CommandLineParseResult(ParsedCommand? Command, string? Error)
{
    public bool IsSuccess => Command is not null;
}

internal static class CommandLine
{
    private const int DefaultTimeoutMs = 180_000;

    public static CommandLineParseResult Parse(string[] args)
    {
        if (args.Length == 0)
        {
            return Fail("An operation is required. Use 'capabilities' to discover callable operations.");
        }

        var operation = args[0];
        if (!OperationRegistry.IsKnown(operation))
        {
            return Fail($"Unknown operation '{operation}'.");
        }

        var compact = false;
        string? tracePath = null;
        string? viewerPath = null;
        var timeoutMs = DefaultTimeoutMs;
        var timeoutSpecified = false;
        var identityMode = "localWeak";
        var identityModeSpecified = false;
        var cursor = 0;
        var cursorSpecified = false;
        var limit = 100;
        var limitSpecified = false;
        int? eventOrdinal = null;
        string? eventPath = null;
        var metricTables = new List<string>();
        string? shaderHash = null;
        int? shaderOccurrence = null;
        var counterNames = new List<string>();
        var rangeNames = new List<string>();
        string? eventName = null;
        int? eventOccurrence = null;
        int? withinEventOrdinal = null;
        string? baselineTracePath = null;
        int? baselineEventOrdinal = null;
        int? targetFrameIndex = null;
        int? baselineFrameIndex = null;
        int? analysisSeedEventOrdinal = null;
        int? presentQueueEventOrdinal = null;
        var topShaderCount = 32;
        var topShaderCountSpecified = false;

        for (var index = 1; index < args.Length; index++)
        {
            var token = args[index];
            switch (token)
            {
                case "--compact":
                    compact = true;
                    break;
                case "--viewer":
                    if (!TryTakeValue(args, ref index, out viewerPath))
                    {
                        return Fail("--viewer requires the exact ngfx-ui.exe path.");
                    }
                    break;
                case "--timeout-ms":
                    if (!TryTakeValue(args, ref index, out var timeoutText) ||
                        !int.TryParse(timeoutText, out timeoutMs) ||
                        timeoutMs is < 1 or > ContractLimits.MaximumTimeoutMs)
                    {
                        return Fail($"--timeout-ms must be an integer from 1 to {ContractLimits.MaximumTimeoutMs}.");
                    }
                    timeoutSpecified = true;
                    break;
                case "--identity-mode":
                    if (!TryTakeValue(args, ref index, out var identityText) ||
                        !TryNormalizeIdentityMode(identityText, out identityMode))
                    {
                        return Fail("--identity-mode must be 'localWeak' or 'sha256'.");
                    }
                    identityModeSpecified = true;
                    break;
                case "--cursor":
                    if (!TryTakeValue(args, ref index, out var cursorText) ||
                        !int.TryParse(cursorText, out cursor) || cursor < 0)
                    {
                        return Fail("--cursor must be a non-negative integer.");
                    }
                    cursorSpecified = true;
                    break;
                case "--limit":
                    if (!TryTakeValue(args, ref index, out var limitText) ||
                        !int.TryParse(limitText, out limit) ||
                        limit is < 1 or > ContractLimits.MaximumPageLimit)
                    {
                        return Fail($"--limit must be an integer from 1 to {ContractLimits.MaximumPageLimit}.");
                    }
                    limitSpecified = true;
                    break;
                case "--event-ordinal":
                    if (!TryTakeValue(args, ref index, out var ordinalText) ||
                        !int.TryParse(ordinalText, out var ordinal) || ordinal < 0)
                    {
                        return Fail("--event-ordinal must be a non-negative integer.");
                    }
                    eventOrdinal = ordinal;
                    break;
                case "--event-path":
                    if (!TryTakeValue(args, ref index, out eventPath) ||
                        !IsValidEventPath(eventPath))
                    {
                        return Fail("--event-path must contain 1 to 64 non-negative row indexes separated by '.'.");
                    }
                    break;
                case "--table":
                    if (!TryTakeValue(args, ref index, out var table) ||
                        string.IsNullOrWhiteSpace(table) || table.Length > 256 ||
                        table.Contains(';'))
                    {
                        return Fail("--table requires an exact semantic table name of at most 256 characters.");
                    }
                    metricTables.Add(table);
                    if (metricTables.Count > ContractLimits.MaximumMetricTableFilters)
                    {
                        return Fail($"At most {ContractLimits.MaximumMetricTableFilters} --table filters are allowed.");
                    }
                    break;
                case "--shader-hash":
                    if (!TryTakeValue(args, ref index, out shaderHash) ||
                        !IsValidShaderHash(shaderHash))
                    {
                        return Fail("--shader-hash must be 0x followed by 16 hexadecimal digits.");
                    }
                    shaderHash = shaderHash!.ToLowerInvariant();
                    break;
                case "--shader-occurrence":
                    if (!TryTakeValue(args, ref index, out var shaderOccurrenceText) ||
                        !int.TryParse(shaderOccurrenceText, out var occurrence) ||
                        occurrence < 0)
                    {
                        return Fail("--shader-occurrence must be a non-negative integer.");
                    }
                    shaderOccurrence = occurrence;
                    break;
                case "--event-name":
                    if (!TryTakeValue(args, ref index, out eventName) ||
                        string.IsNullOrWhiteSpace(eventName) || eventName.Length > 512)
                    {
                        return Fail("--event-name requires an exact non-empty name of at most 512 characters.");
                    }
                    break;
                case "--event-occurrence":
                    if (!TryTakeValue(args, ref index, out var eventOccurrenceText) ||
                        !int.TryParse(eventOccurrenceText, out var parsedEventOccurrence) ||
                        parsedEventOccurrence < 0)
                    {
                        return Fail("--event-occurrence must be a non-negative integer.");
                    }
                    eventOccurrence = parsedEventOccurrence;
                    break;
                case "--within-event-ordinal":
                    if (!TryTakeValue(args, ref index, out var withinOrdinalText) ||
                        !int.TryParse(withinOrdinalText, out var parsedWithinOrdinal) ||
                        parsedWithinOrdinal < 0)
                    {
                        return Fail("--within-event-ordinal must be a non-negative integer.");
                    }
                    withinEventOrdinal = parsedWithinOrdinal;
                    break;
                case "--baseline-trace":
                    if (baselineTracePath is not null ||
                        !TryTakeValue(args, ref index, out baselineTracePath))
                    {
                        return Fail("--baseline-trace requires exactly one .ngfx-gputrace path.");
                    }
                    break;
                case "--baseline-event-ordinal":
                    if (!TryTakeValue(args, ref index, out var baselineOrdinalText) ||
                        !int.TryParse(baselineOrdinalText, out var parsedBaselineOrdinal) ||
                        parsedBaselineOrdinal < 0)
                    {
                        return Fail("--baseline-event-ordinal must be a non-negative integer.");
                    }
                    baselineEventOrdinal = parsedBaselineOrdinal;
                    break;
                case "--target-frame-index":
                    if (!TryTakeValue(args, ref index, out var targetFrameIndexText) ||
                        !int.TryParse(targetFrameIndexText, out var parsedTargetFrameIndex) ||
                        parsedTargetFrameIndex < 1)
                    {
                        return Fail("--target-frame-index must be a positive integer.");
                    }
                    targetFrameIndex = parsedTargetFrameIndex;
                    break;
                case "--baseline-frame-index":
                    if (!TryTakeValue(args, ref index, out var baselineFrameIndexText) ||
                        !int.TryParse(baselineFrameIndexText, out var parsedBaselineFrameIndex) ||
                        parsedBaselineFrameIndex < 1)
                    {
                        return Fail("--baseline-frame-index must be a positive integer.");
                    }
                    baselineFrameIndex = parsedBaselineFrameIndex;
                    break;
                case "--analysis-seed-event-ordinal":
                    if (!TryTakeValue(args, ref index, out var analysisSeedOrdinalText) ||
                        !int.TryParse(analysisSeedOrdinalText, out var parsedAnalysisSeedOrdinal) ||
                        parsedAnalysisSeedOrdinal < 0)
                    {
                        return Fail("--analysis-seed-event-ordinal must be a non-negative integer.");
                    }
                    analysisSeedEventOrdinal = parsedAnalysisSeedOrdinal;
                    break;
                case "--present-queue-event-ordinal":
                    if (!TryTakeValue(args, ref index, out var presentQueueOrdinalText) ||
                        !int.TryParse(presentQueueOrdinalText, out var parsedPresentQueueOrdinal) ||
                        parsedPresentQueueOrdinal < 0)
                    {
                        return Fail("--present-queue-event-ordinal must be a non-negative integer.");
                    }
                    presentQueueEventOrdinal = parsedPresentQueueOrdinal;
                    break;
                case "--top-shaders":
                    if (!TryTakeValue(args, ref index, out var topShaderText) ||
                        !int.TryParse(topShaderText, out topShaderCount) ||
                        topShaderCount is < 1 or > 100)
                    {
                        return Fail("--top-shaders must be an integer from 1 to 100.");
                    }
                    topShaderCountSpecified = true;
                    break;
                case "--counter":
                    if (!TryTakeValue(args, ref index, out var counterName) ||
                        string.IsNullOrWhiteSpace(counterName) ||
                        counterName.Length > 512 || counterName.Contains(';'))
                    {
                        return Fail("--counter requires an exact counter name of at most 512 characters.");
                    }
                    counterNames.Add(counterName);
                    if (counterNames.Count > ContractLimits.MaximumCounterFilters)
                    {
                        return Fail($"At most {ContractLimits.MaximumCounterFilters} --counter filters are allowed.");
                    }
                    break;
                case "--range":
                    if (!TryTakeValue(args, ref index, out var rangeName) ||
                        string.IsNullOrWhiteSpace(rangeName) ||
                        rangeName.Length > 512 || rangeName.Contains(';'))
                    {
                        return Fail("--range requires an exact exported range name of at most 512 characters.");
                    }
                    rangeNames.Add(rangeName);
                    if (rangeNames.Count > ContractLimits.MaximumRangeFilters)
                    {
                        return Fail($"At most {ContractLimits.MaximumRangeFilters} --range filters are allowed.");
                    }
                    break;
                default:
                    if (token.StartsWith('-'))
                    {
                        return Fail($"Unknown option '{token}'.");
                    }
                    if (operation == "capabilities")
                    {
                        return Fail("capabilities does not accept a trace path.");
                    }
                    if (tracePath is not null)
                    {
                        return Fail($"{operation} accepts exactly one trace path.");
                    }
                    tracePath = token;
                    break;
            }
        }

        var isTraceOperation = operation.StartsWith("trace.", StringComparison.Ordinal);
        var isWrapperOperation = operation is
            "resolve-event" or "inspect-pass" or "compare-ranges" or
            "compare-frame-timing";
        var requiresTrace = isTraceOperation || isWrapperOperation ||
            operation == "viewer-session.close";
        if (requiresTrace && string.IsNullOrWhiteSpace(tracePath))
        {
            return Fail($"{operation} requires a .ngfx-gputrace path.");
        }
        if (!requiresTrace && (viewerPath is not null || timeoutSpecified))
        {
            return Fail($"{operation} does not accept Viewer options.");
        }
        if (identityModeSpecified && operation != "trace.info")
        {
            return Fail($"{operation} does not accept --identity-mode.");
        }
        if ((cursorSpecified || limitSpecified) &&
            operation is not ("trace.events" or "trace.range-metrics" or
                "trace.range-shaders" or "trace.shader-source" or
                "trace.analysis" or "trace.counter-catalog" or
                "trace.range-counters" or "trace.range-instruction-mix" or
                "compare-ranges"))
        {
            return Fail($"{operation} does not accept paging options.");
        }

        var isAtomicScoped = operation is
            "trace.event-parameters" or "trace.range-metrics" or
            "trace.range-shaders" or "trace.shader-source" or
            "trace.analysis" or "trace.counter-catalog" or
            "trace.range-counters" or "trace.range-instruction-mix";
        if (isAtomicScoped && (eventOrdinal is null) == (eventPath is null))
        {
            return Fail($"{operation} requires exactly one of --event-ordinal or --event-path.");
        }
        if (operation == "inspect-pass" &&
            (eventOrdinal is null || eventPath is not null))
        {
            return Fail("inspect-pass requires --event-ordinal and does not accept --event-path.");
        }
        if (operation == "compare-ranges" &&
            (eventOrdinal is null || eventPath is not null || baselineEventOrdinal is null))
        {
            return Fail(
                "compare-ranges requires --event-ordinal and --baseline-event-ordinal, and does not accept --event-path.");
        }
        if (operation == "compare-frame-timing" &&
            (eventOrdinal is null || eventPath is not null ||
                baselineEventOrdinal is null || targetFrameIndex is null ||
                baselineFrameIndex is null || analysisSeedEventOrdinal is null ||
                presentQueueEventOrdinal is null))
        {
            return Fail(
                "compare-frame-timing requires --event-ordinal, --target-frame-index, " +
                "--baseline-event-ordinal, --baseline-frame-index, " +
                "--analysis-seed-event-ordinal, and --present-queue-event-ordinal; " +
                "it does not accept --event-path.");
        }
        var acceptsEventSelector = isAtomicScoped ||
            operation is "inspect-pass" or "compare-ranges" or "compare-frame-timing";
        if (!acceptsEventSelector && (eventOrdinal is not null || eventPath is not null))
        {
            return Fail($"{operation} does not accept an event selector.");
        }
        if (metricTables.Count > 0 &&
            operation is not ("trace.range-metrics" or "inspect-pass" or "compare-ranges"))
        {
            return Fail($"{operation} does not accept --table.");
        }
        if (shaderHash is not null &&
            operation is not ("trace.range-shaders" or "trace.shader-source"))
        {
            return Fail($"{operation} does not accept --shader-hash.");
        }
        if (shaderOccurrence is not null &&
            operation is not ("trace.range-shaders" or "trace.shader-source"))
        {
            return Fail($"{operation} does not accept --shader-occurrence.");
        }
        if (shaderOccurrence is not null && shaderHash is null)
        {
            return Fail("--shader-occurrence requires --shader-hash.");
        }
        if (operation == "trace.shader-source" && shaderHash is null)
        {
            return Fail("trace.shader-source requires --shader-hash.");
        }
        if (counterNames.Count > 0 && operation != "trace.range-counters")
        {
            return Fail($"{operation} does not accept --counter.");
        }
        if (rangeNames.Count > 0 && operation != "trace.range-counters")
        {
            return Fail($"{operation} does not accept --range.");
        }
        if (operation == "trace.range-counters" && counterNames.Count == 0)
        {
            return Fail("trace.range-counters requires at least one --counter filter.");
        }
        if (operation == "resolve-event" &&
            (eventName is null || eventOccurrence is null))
        {
            return Fail("resolve-event requires --event-name and --event-occurrence.");
        }
        if (operation != "resolve-event" &&
            (eventName is not null || eventOccurrence is not null ||
                withinEventOrdinal is not null))
        {
            return Fail($"{operation} does not accept event-resolution options.");
        }
        if (operation is not ("compare-ranges" or "compare-frame-timing") &&
            (baselineTracePath is not null || baselineEventOrdinal is not null))
        {
            return Fail($"{operation} does not accept baseline options.");
        }
        if (operation == "compare-frame-timing" && baselineTracePath is not null)
        {
            return Fail("compare-frame-timing currently compares two frames from the same trace and does not accept --baseline-trace.");
        }
        if (operation != "compare-frame-timing" &&
            (targetFrameIndex is not null || baselineFrameIndex is not null ||
                analysisSeedEventOrdinal is not null || presentQueueEventOrdinal is not null))
        {
            return Fail($"{operation} does not accept frame-timing options.");
        }
        if (topShaderCountSpecified &&
            operation is not ("inspect-pass" or "compare-ranges"))
        {
            return Fail($"{operation} does not accept --top-shaders.");
        }

        return new(new(
            operation,
            compact,
            tracePath,
            viewerPath,
            timeoutMs,
            identityMode,
            cursor,
            limit,
            eventOrdinal,
            eventPath,
            metricTables,
            shaderHash,
            shaderOccurrence,
            counterNames,
            rangeNames,
            eventName,
            eventOccurrence,
            withinEventOrdinal,
            baselineTracePath,
            baselineEventOrdinal,
            targetFrameIndex,
            baselineFrameIndex,
            analysisSeedEventOrdinal,
            presentQueueEventOrdinal,
            topShaderCount), null);
    }

    private static bool TryTakeValue(string[] args, ref int index, out string? value)
    {
        if (index + 1 >= args.Length || args[index + 1].StartsWith('-'))
        {
            value = null;
            return false;
        }
        value = args[++index];
        return true;
    }

    private static bool TryNormalizeIdentityMode(string? value, out string normalized)
    {
        if (value?.Equals("localWeak", StringComparison.OrdinalIgnoreCase) == true)
        {
            normalized = "localWeak";
            return true;
        }
        if (value?.Equals("sha256", StringComparison.OrdinalIgnoreCase) == true)
        {
            normalized = "sha256";
            return true;
        }
        normalized = string.Empty;
        return false;
    }

    private static bool IsValidEventPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }
        var parts = value.Split('.');
        return parts.Length is >= 1 and <= 64 &&
            parts.All(part => int.TryParse(part, out var row) && row >= 0);
    }

    private static bool IsValidShaderHash(string? value) =>
        value is { Length: 18 } &&
        value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
        value[2..].All(character =>
            character is >= '0' and <= '9' ||
            character is >= 'a' and <= 'f' ||
            character is >= 'A' and <= 'F');

    private static CommandLineParseResult Fail(string message) => new(null, message);
}
