using NsightAnalyzer.Adapters.NsightViewer2026_2;
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
    bool EventNameContains,
    int? EventOccurrence,
    int? WithinEventOrdinal,
    string? BaselineTracePath,
    int? BaselineEventOrdinal,
    int? TargetFrameIndex,
    int? BaselineFrameIndex,
    int? AnalysisSeedEventOrdinal,
    int? PresentQueueEventOrdinal,
    int TopShaderCount,
    string Grain,
    string? RangeNameContains,
    string? ShaderStage,
    bool Detail,
    RangeSections Sections,
    string? DescribedOperation,
    bool IncludeUnchanged,
    string? Workspace,
    string? SourceView,
    int SourceViewOccurrence,
    int? MetricSourceOrdinal,
    string? PairsFile);

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
            return Fail(
                $"Unknown operation '{operation}'. Run '--help' for the callable catalog.");
        }

        var compact = true;
        var detail = false;
        var includeUnchanged = false;
        string? describedOperation = null;
        var sections = operation == "inspect-pass"
            ? RangeSections.Metrics | RangeSections.Shaders
            : RangeSections.Metrics;
        var sectionsSpecified = false;
        string? tracePath = null;
        string? viewerPath = null;
        string? workspace = null;
        string? sourceView = null;
        var sourceViewOccurrence = 0;
        int? metricSourceOrdinal = null;
        string? pairsFile = null;
        var timeoutMs = DefaultTimeoutMs;
        var timeoutSpecified = false;
        var identityMode = "localWeak";
        var identityModeSpecified = false;
        var grain = operation == "find-ranges" ? EventGrain.Marker : EventGrain.All;
        var grainSpecified = false;
        var cursor = 0;
        var cursorSpecified = false;
        var limit = ContractLimits.DefaultPageLimit;
        var limitSpecified = false;
        int? eventOrdinal = null;
        string? eventPath = null;
        var metricTables = new List<string>();
        string? shaderHash = null;
        string? shaderStage = null;
        int? shaderOccurrence = null;
        var counterNames = new List<string>();
        var rangeNames = new List<string>();
        string? eventName = null;
        var eventNameContains = false;
        int? eventOccurrence = null;
        int? withinEventOrdinal = null;
        string? baselineTracePath = null;
        int? baselineEventOrdinal = null;
        int? targetFrameIndex = null;
        int? baselineFrameIndex = null;
        int? analysisSeedEventOrdinal = null;
        int? presentQueueEventOrdinal = null;
        var topShaderCount = ContractLimits.DefaultTopShaders;
        var topShaderCountSpecified = false;
        string? rangeNameContains = null;
        var seenOptions = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 1; index < args.Length; index++)
        {
            var token = args[index];
            if (token.StartsWith('-'))
            {
                var parameter = OperationRegistry.ParameterFor(operation, token);
                if (parameter is null)
                {
                    return Fail($"Unknown option '{token}' for {operation}. Use 'describe {operation}'.");
                }
                if (!seenOptions.Add(token) && !parameter.Repeatable)
                {
                    return Fail($"{token} cannot be repeated; pass exactly one value.");
                }
                if (seenOptions.Contains("--pretty") && seenOptions.Contains("--compact"))
                {
                    return Fail("--pretty and --compact are mutually exclusive; concise JSON is the default.");
                }
            }
            switch (token)
            {
                case "--compact":
                    compact = true;
                    break;
                case "--pretty":
                    compact = false;
                    break;
                case "--detail":
                    detail = true;
                    break;
                case "--include-unchanged":
                    includeUnchanged = true;
                    break;
                case "--sections":
                    if (!TryTakeValue(args, ref index, out var sectionsText) ||
                        !RangeSectionNames.TryParse(sectionsText!, out sections))
                    {
                        return Fail("--sections requires comma-separated timing, metrics, shaders, or instruction-mix without duplicates.");
                    }
                    sectionsSpecified = true;
                    break;
                case "--viewer":
                    if (!TryTakeValue(args, ref index, out viewerPath))
                    {
                        return Fail("--viewer requires the exact ngfx-ui.exe path.");
                    }
                    break;
                case "--workspace":
                    if (!TryTakeValue(args, ref index, out var workspaceText) ||
                        string.IsNullOrWhiteSpace(workspaceText) ||
                        !Path.IsPathFullyQualified(workspaceText))
                    {
                        return Fail("--workspace requires an absolute directory path.");
                    }
                    try
                    {
                        workspace = Path.GetFullPath(workspaceText);
                    }
                    catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
                    {
                        return Fail("--workspace requires a valid absolute directory path.");
                    }
                    if (File.Exists(workspace))
                    {
                        return Fail("--workspace must name a directory, not an existing file.");
                    }
                    break;
                case "--pairs-file":
                    if (!TryTakeValue(args, ref index, out pairsFile) || string.IsNullOrWhiteSpace(pairsFile))
                    {
                        return Fail("--pairs-file requires a local JSON file of explicit target/baseline EventKey pairs.");
                    }
                    break;
                case "--view":
                    if (!TryTakeValue(args, ref index, out sourceView) ||
                        sourceView is not ("dxil" or "sass" or "hlsl" or "source"))
                    {
                        return Fail("--view must be dxil, sass, hlsl, or source.");
                    }
                    break;
                case "--view-occurrence":
                    if (!TryTakeValue(args, ref index, out var viewOccurrenceText) ||
                        !int.TryParse(viewOccurrenceText, out sourceViewOccurrence) || sourceViewOccurrence < 0)
                    {
                        return Fail("--view-occurrence must be a non-negative integer.");
                    }
                    break;
                case "--source-ordinal":
                    if (!TryTakeValue(args, ref index, out var sourceOrdinalText) ||
                        !int.TryParse(sourceOrdinalText, out var sourceOrdinal) || sourceOrdinal < 0)
                    {
                        return Fail("--source-ordinal must be a non-negative integer.");
                    }
                    metricSourceOrdinal = sourceOrdinal;
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
                case "--grain":
                    if (!TryTakeValue(args, ref index, out var grainText) ||
                        grainText is not (EventGrain.Marker or EventGrain.Container
                            or EventGrain.All))
                    {
                        return Fail(
                            "--grain must be 'marker', 'container', or 'all'.");
                    }
                    grain = grainText;
                    grainSpecified = true;
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
                    // The upper bound depends on how large one row of the
                    // requested fact family can be, so it is checked once the
                    // operation is known rather than against a global maximum.
                    if (!TryTakeValue(args, ref index, out var limitText) ||
                        !int.TryParse(limitText, out limit) || limit < 1)
                    {
                        return Fail("--limit must be a positive integer.");
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
                case "--shader-stage":
                    if (!TryTakeValue(args, ref index, out shaderStage) ||
                        string.IsNullOrWhiteSpace(shaderStage) ||
                        shaderStage.Length > 64)
                    {
                        return Fail("--shader-stage requires a non-empty semantic stage of at most 64 characters.");
                    }
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
                case "--event-name-mode":
                    if (!TryTakeValue(args, ref index, out var eventNameModeText) ||
                        eventNameModeText is not ("exact" or "contains"))
                    {
                        return Fail("--event-name-mode must be 'exact' or 'contains'.");
                    }
                    eventNameContains = eventNameModeText == "contains";
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
                case "--name-contains":
                    if (!TryTakeValue(args, ref index, out rangeNameContains) ||
                        string.IsNullOrWhiteSpace(rangeNameContains) ||
                        rangeNameContains.Length > 512)
                    {
                        return Fail("--name-contains requires a non-empty substring of at most 512 characters.");
                    }
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
                    if (operation is "capabilities" or "doctor" or "version")
                    {
                        return Fail($"{operation} does not accept a trace path.");
                    }
                    if (operation == "describe")
                    {
                        if (describedOperation is not null)
                        {
                            return Fail("describe accepts exactly one operation name.");
                        }
                        describedOperation = token;
                        break;
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
        if (operation == "describe" && describedOperation is null)
        {
            return Fail("describe requires an operation name.");
        }
        if (detail && operation is not ("capabilities" or "trace.range-metrics" or
                "trace.range-shaders" or "inspect-pass" or "compare-ranges" or "find-metrics"))
        {
            return Fail($"{operation} already returns its requested detail and does not accept --detail.");
        }
        if (sectionsSpecified && operation is not ("inspect-pass" or "compare-ranges"))
        {
            return Fail($"{operation} does not accept --sections.");
        }
        if (includeUnchanged && (operation != "compare-ranges" || !sections.HasFlag(RangeSections.Metrics)))
        {
            return Fail("--include-unchanged requires the compare-ranges metrics section.");
        }
        var isWrapperOperation = operation is
            "resolve-event" or "find-events" or "find-ranges" or "inspect-pass" or "compare-ranges" or
            "compare-frame-timing" or "compare-timings" or "find-metrics" or "find-source-hotspots";
        var requiresTrace = isTraceOperation || isWrapperOperation ||
            operation == "viewer-session.close";
        if (requiresTrace && string.IsNullOrWhiteSpace(tracePath))
        {
            return Fail($"{operation} requires a .ngfx-gputrace path.");
        }
        if (!requiresTrace && operation != "doctor" && (viewerPath is not null || timeoutSpecified))
        {
            return Fail($"{operation} does not accept Viewer options.");
        }
        if (!limitSpecified && operation == "find-events")
        {
            limit = 20;
        }
        var maximumLimit = MaximumLimitFor(operation);
        if (limit > maximumLimit)
        {
            return Fail(
                $"--limit must be an integer from 1 to {maximumLimit} for {operation}.");
        }
        if (grainSpecified && operation is not ("trace.outline" or "find-ranges"))
        {
            return Fail($"{operation} does not accept --grain.");
        }
        if (identityModeSpecified && operation != "trace.info")
        {
            return Fail($"{operation} does not accept --identity-mode.");
        }
        if ((cursorSpecified || limitSpecified) &&
            operation is not ("trace.events" or "trace.outline" or
                "trace.range-metric-catalog" or "trace.range-metrics" or
                "trace.range-shaders" or "trace.shader-source" or
                "trace.analysis" or "trace.counter-catalog" or
                "trace.range-counters" or "trace.range-instruction-mix" or
                "find-events" or "find-ranges" or "inspect-pass" or "compare-ranges" or
                "find-metrics" or "find-source-hotspots" or "compare-timings"))
        {
            return Fail($"{operation} does not accept paging options.");
        }

        var isAtomicScoped = operation is
            "trace.event-parameters" or "trace.range-metrics" or
            "trace.range-metric-catalog" or "trace.range-shaders" or
            "trace.shader-profile" or "trace.shader-source" or
            "trace.analysis" or "trace.counter-catalog" or
            "trace.range-counters" or "trace.range-instruction-mix" or "find-source-hotspots";
        if (isAtomicScoped && (eventOrdinal is null) == (eventPath is null))
        {
            return Fail($"{operation} requires exactly one of --event-ordinal or --event-path.");
        }
        if ((operation is "inspect-pass" or "find-metrics") &&
            (eventOrdinal is null || eventPath is not null))
        {
            return Fail($"{operation} requires --event-ordinal and does not accept --event-path.");
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
            operation is "inspect-pass" or "compare-ranges" or "compare-frame-timing" or "find-metrics";
        if (!acceptsEventSelector && (eventOrdinal is not null || eventPath is not null))
        {
            return Fail($"{operation} does not accept an event selector.");
        }
        if (metricTables.Count > 0 &&
            operation is not ("trace.range-metrics" or "inspect-pass" or "compare-ranges" or "find-metrics"))
        {
            return Fail($"{operation} does not accept --table.");
        }
        if (operation is "inspect-pass" or "compare-ranges")
        {
            if (!sections.HasFlag(RangeSections.Metrics) &&
                (metricTables.Count > 0 || cursorSpecified || limitSpecified))
            {
                return Fail("--table, --cursor and --limit require the metrics section; shader output uses --top-shaders.");
            }
            if (topShaderCountSpecified && !sections.HasFlag(RangeSections.Shaders))
            {
                return Fail("--top-shaders requires the shaders section.");
            }
        }
        if (shaderHash is not null &&
            operation is not ("trace.range-shaders" or "trace.shader-profile" or
                "trace.shader-source" or "find-source-hotspots"))
        {
            return Fail($"{operation} does not accept --shader-hash.");
        }
        if (shaderOccurrence is not null &&
            operation is not ("trace.range-shaders" or "trace.shader-profile" or
                "trace.shader-source" or "find-source-hotspots"))
        {
            return Fail($"{operation} does not accept --shader-occurrence.");
        }
        if (shaderOccurrence is not null && shaderHash is null)
        {
            return Fail("--shader-occurrence requires --shader-hash.");
        }
        if ((operation is "trace.shader-source" or "find-source-hotspots") && (shaderHash is null || shaderStage is null))
        {
            return Fail($"{operation} requires --shader-stage and --shader-hash.");
        }
        if (operation == "trace.shader-profile" &&
            (shaderHash is null || shaderStage is null))
        {
            return Fail("trace.shader-profile requires --shader-stage and --shader-hash.");
        }
        if (operation is not ("trace.shader-profile" or "trace.shader-source" or "find-source-hotspots") && shaderStage is not null)
        {
            return Fail($"{operation} does not accept --shader-stage.");
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
        if (operation != "resolve-event" && eventNameContains)
        {
            return Fail($"{operation} does not accept --event-name-mode.");
        }
        if (operation != "resolve-event" &&
            (eventName is not null || eventOccurrence is not null))
        {
            return Fail($"{operation} does not accept event-resolution options.");
        }
        if (withinEventOrdinal is not null &&
            operation is not ("resolve-event" or "find-events" or "find-ranges"))
        {
            return Fail($"{operation} does not accept --within-event-ordinal.");
        }
        if (operation == "find-events" && rangeNameContains is null)
        {
            return Fail("find-events requires --name-contains.");
        }
        if (operation == "find-metrics" && (rangeNameContains is null) == (metricSourceOrdinal is null))
        {
            return Fail("find-metrics requires exactly one of --name-contains or --source-ordinal.");
        }
        if (operation == "find-source-hotspots" && sourceView is null)
        {
            return Fail("find-source-hotspots requires --view.");
        }
        if (operation == "compare-timings" && pairsFile is null)
        {
            return Fail("compare-timings requires --pairs-file.");
        }
        if (rangeNameContains is not null &&
            operation is not ("find-events" or "find-ranges" or "find-metrics"))
        {
            return Fail($"{operation} does not accept --name-contains.");
        }
        if (operation is not ("compare-ranges" or "compare-frame-timing" or "compare-timings") &&
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
            eventNameContains,
            eventOccurrence,
            withinEventOrdinal,
            baselineTracePath,
            baselineEventOrdinal,
            targetFrameIndex,
            baselineFrameIndex,
            analysisSeedEventOrdinal,
            presentQueueEventOrdinal,
            topShaderCount,
            grain,
            rangeNameContains,
            shaderStage,
            detail,
            sections,
            describedOperation,
            includeUnchanged,
            workspace,
            sourceView,
            sourceViewOccurrence,
            metricSourceOrdinal,
            pairsFile), null);
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

    /// <summary>
    /// The largest page this operation can actually serve. Shader rows carry
    /// their instruction mix and stall reasons, so a full-size page of them
    /// would breach the response bound after the work was already spent.
    /// </summary>
    internal static int MaximumLimitFor(string operation) =>
        operation switch
        {
            "trace.range-shaders" => ContractLimits.MaximumShaderPageLimit,
            "find-events" => ContractLimits.MaximumEventSearchPageLimit,
            _ => ContractLimits.MaximumPageLimit,
        };

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
