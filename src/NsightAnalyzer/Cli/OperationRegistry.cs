using NsightAnalyzer.Contracts;
using NsightAnalyzer.Operations;
using NsightAnalyzer.Wrappers;

namespace NsightAnalyzer.Cli;

internal sealed record OperationDefinition(
    OperationDescriptor Descriptor,
    Func<ParsedCommand, Task<OperationResult>> Handler,
    bool Discoverable = true);

internal static class OperationRegistry
{
    private static readonly IReadOnlyList<OperationDefinition> Definitions =
    [
        new(
            new(
                "capabilities",
                SchemaVersion.V1,
                "readOnly",
                false,
                "verified",
                "Returns the currently callable GPU Trace atom and wrapper catalog.",
                "capabilities [--compact]",
                Parameters: [OperationParameters.Compact]),
            _ => Task.FromResult(
                CapabilitiesOperation.Execute(GetPublicDescriptors()))),
        new(
            new(
                "trace.info",
                SchemaVersion.V1,
                "readOnly",
                true,
                "implemented",
                "Returns trace identity and verifies the pinned Viewer decoder/bridge.",
                "trace.info <trace> [--identity-mode localWeak|sha256] [--viewer <path>]",
                Parameters: [OperationParameters.Trace, new("--identity-mode", "string", false, "How the trace snapshot is identified.", Default: "localWeak", AllowedValues: ["localWeak", "sha256"]), OperationParameters.Viewer, OperationParameters.TimeoutMs, OperationParameters.Compact]),
            command => TraceInfoOperation.ExecuteAsync(
                command.TracePath!,
                command.ViewerPath,
                command.TimeoutMs,
                command.IdentityMode)),
        new(
            new(
                "trace.events",
                SchemaVersion.V1,
                "readOnly",
                true,
                "implemented",
                "Returns a bounded page of semantic Event List facts and exact EventKeys.",
                "trace.events <trace> [--cursor N] [--limit N] [--viewer <path>]",
                Parameters: [OperationParameters.Trace, OperationParameters.Cursor, OperationParameters.Limit, OperationParameters.Viewer, OperationParameters.TimeoutMs, OperationParameters.Compact]),
            command => TraceEventsOperation.ExecuteAsync(
                command.TracePath!,
                command.ViewerPath,
                command.TimeoutMs,
                command.Cursor,
                command.Limit)),
        new(
            new(
                "trace.outline",
                SchemaVersion.V1,
                "readOnly",
                true,
                "implemented",
                "Returns the range-grain skeleton: every pass/marker and command-list range with its timing, without per-draw noise.",
                "trace.outline <trace> [--grain marker|container|all] [--cursor N] [--limit N] [--viewer <path>]",
                Parameters: [OperationParameters.Trace, new("--grain", "string", false, "Which range kind to return. 'marker' is the caller's own instrumentation; 'container' is the D3D12 objects and calls that structure the capture. Filtered in the decoder, so a narrower grain costs fewer pages.", Default: "all", AllowedValues: ["marker", "container", "all"]), OperationParameters.Cursor, OperationParameters.Limit, OperationParameters.Viewer, OperationParameters.TimeoutMs, OperationParameters.Compact]),
            command => TraceOutlineOperation.ExecuteAsync(
                command.TracePath!,
                command.ViewerPath,
                command.TimeoutMs,
                command.Cursor,
                command.Limit,
                command.Grain)),
        new(
            new(
                "trace.event-parameters",
                SchemaVersion.V1,
                "readOnly",
                true,
                "implemented",
                "Returns structured parameters for one exact EventKey.",
                "trace.event-parameters <trace> (--event-ordinal N|--event-path P)",
                Parameters: [OperationParameters.Trace, OperationParameters.EventOrdinal, OperationParameters.EventPath, OperationParameters.Viewer, OperationParameters.TimeoutMs, OperationParameters.Compact]),
            command => TraceEventParametersOperation.ExecuteAsync(
                command.TracePath!,
                command.ViewerPath,
                command.TimeoutMs,
                command.EventOrdinal,
                command.EventPath)),
        new(
            new(
                "trace.range-metric-catalog",
                SchemaVersion.V1,
                "readOnly",
                true,
                "implemented",
                "Returns compact Warp Metrics table and column identities for one exact pass/marker range.",
                "trace.range-metric-catalog <trace> (--event-ordinal N|--event-path P) " +
                "[--cursor N] [--limit N]",
                Parameters: [OperationParameters.Trace, OperationParameters.EventOrdinal, OperationParameters.EventPath, OperationParameters.Cursor, OperationParameters.Limit, OperationParameters.Viewer, OperationParameters.TimeoutMs, OperationParameters.Compact]),
            command => TraceRangeMetricCatalogOperation.ExecuteAsync(
                command.TracePath!,
                command.ViewerPath,
                command.TimeoutMs,
                command.EventOrdinal,
                command.EventPath,
                command.Cursor,
                command.Limit)),
        new(
            new(
                "trace.range-metrics",
                SchemaVersion.V1,
                "readOnly",
                true,
                "implemented",
                "Returns long-form Warp Metrics for one exact pass/marker EventKey.",
                "trace.range-metrics <trace> (--event-ordinal N|--event-path P) " +
                "[--table <exact>] [--cursor N] [--limit N]",
                Parameters: [OperationParameters.Trace, OperationParameters.EventOrdinal, OperationParameters.EventPath, OperationParameters.MetricTable, OperationParameters.Cursor, OperationParameters.Limit, OperationParameters.Viewer, OperationParameters.TimeoutMs, OperationParameters.Compact]),
            command => TraceRangeMetricsOperation.ExecuteAsync(
                command.TracePath!,
                command.ViewerPath,
                command.TimeoutMs,
                command.EventOrdinal,
                command.EventPath,
                command.MetricTables,
                command.Cursor,
                command.Limit)),
        new(
            new(
                "trace.range-shaders",
                SchemaVersion.V1,
                "readOnly",
                true,
                "implemented",
                "Returns paged static/dynamic shader facts for one exact range.",
                "trace.range-shaders <trace> (--event-ordinal N|--event-path P) " +
                "[--shader-hash H] [--shader-occurrence N] [--cursor N] [--limit N]",
                Parameters: [OperationParameters.Trace, OperationParameters.EventOrdinal, OperationParameters.EventPath, OperationParameters.ShaderHash, OperationParameters.ShaderOccurrence, OperationParameters.Cursor, OperationParameters.ShaderLimit, OperationParameters.Viewer, OperationParameters.TimeoutMs, OperationParameters.Compact]),
            command => TraceRangeShadersOperation.ExecuteAsync(
                command.TracePath!,
                command.ViewerPath,
                command.TimeoutMs,
                command.EventOrdinal,
                command.EventPath,
                command.ShaderHash,
                command.ShaderOccurrence,
                command.Cursor,
                command.Limit)),
        new(
            new(
                "trace.shader-profile",
                SchemaVersion.V1,
                "readOnly",
                true,
                "implemented",
                "Returns one exact shader's static, sampled, instruction and stall profile.",
                "trace.shader-profile <trace> (--event-ordinal N|--event-path P) " +
                "--shader-stage S --shader-hash H [--shader-occurrence N]",
                Parameters: [OperationParameters.Trace, OperationParameters.EventOrdinal, OperationParameters.EventPath, OperationParameters.ShaderStage, OperationParameters.RequiredShaderHash, OperationParameters.ShaderOccurrence, OperationParameters.Viewer, OperationParameters.TimeoutMs, OperationParameters.Compact]),
            command => TraceShaderProfileOperation.ExecuteAsync(
                command.TracePath!,
                command.ViewerPath,
                command.TimeoutMs,
                command.EventOrdinal,
                command.EventPath,
                command.ShaderStage!,
                command.ShaderHash!,
                command.ShaderOccurrence ?? 0)),
        new(
            new(
                "trace.range-instruction-mix",
                SchemaVersion.V1,
                "readOnly",
                true,
                "implemented",
                "Returns dynamic range-level instruction categories, samples, and stalls.",
                "trace.range-instruction-mix <trace> " +
                "(--event-ordinal N|--event-path P) [--cursor N] [--limit N]",
                Parameters: [OperationParameters.Trace, OperationParameters.EventOrdinal, OperationParameters.EventPath, OperationParameters.Cursor, OperationParameters.Limit, OperationParameters.Viewer, OperationParameters.TimeoutMs, OperationParameters.Compact]),
            command => TraceRangeInstructionMixOperation.ExecuteAsync(
                command.TracePath!,
                command.ViewerPath,
                command.TimeoutMs,
                command.EventOrdinal,
                command.EventPath,
                command.Cursor,
                command.Limit)),
        new(
            new(
                "trace.shader-source",
                SchemaVersion.V1,
                "readOnly",
                true,
                "implemented",
                "Returns paged DXIL/SASS-correlated hotspot rows for one exact shader.",
                "trace.shader-source <trace> (--event-ordinal N|--event-path P) " +
                "--shader-hash H [--shader-occurrence N] [--cursor N] [--limit N]",
                Parameters: [OperationParameters.Trace, OperationParameters.EventOrdinal, OperationParameters.EventPath, new("--shader-hash", "string", true, "Shader hash as 0x followed by 16 hexadecimal digits."), OperationParameters.ShaderOccurrence, OperationParameters.Cursor, OperationParameters.Limit, OperationParameters.Viewer, OperationParameters.TimeoutMs, OperationParameters.Compact]),
            command => TraceShaderSourceOperation.ExecuteAsync(
                command.TracePath!,
                command.ViewerPath,
                command.TimeoutMs,
                command.EventOrdinal,
                command.EventPath,
                command.ShaderHash!,
                command.ShaderOccurrence ?? 0,
                command.Cursor,
                command.Limit)),
        new(
            new(
                "trace.analysis",
                SchemaVersion.V1,
                "readOnly",
                true,
                "implemented",
                "Returns Viewer Trace Analysis ranges, top issues, and annotations.",
                "trace.analysis <trace> (--event-ordinal N|--event-path P) " +
                "[--cursor N] [--limit N] [--viewer <path>]",
                Parameters: [OperationParameters.Trace, OperationParameters.EventOrdinal, OperationParameters.EventPath, OperationParameters.Cursor, OperationParameters.Limit, OperationParameters.Viewer, OperationParameters.TimeoutMs, OperationParameters.Compact]),
            command => TraceAnalysisOperation.ExecuteAsync(
                command.TracePath!,
                command.ViewerPath,
                command.TimeoutMs,
                command.EventOrdinal,
                command.EventPath,
                command.Cursor,
                command.Limit)),
        new(
            new(
                "trace.counter-catalog",
                SchemaVersion.V1,
                "writesLocalArtifacts",
                true,
                "implemented",
                "Exports and returns the raw GPU counter column catalog.",
                "trace.counter-catalog <trace> (--event-ordinal N|--event-path P) " +
                "[--cursor N] [--limit N] [--viewer <path>]",
                Parameters: [OperationParameters.Trace, OperationParameters.EventOrdinal, OperationParameters.EventPath, OperationParameters.Cursor, OperationParameters.Limit, OperationParameters.Viewer, OperationParameters.TimeoutMs, OperationParameters.Compact]),
            command => TraceCounterCatalogOperation.ExecuteAsync(
                command.TracePath!,
                command.ViewerPath,
                command.TimeoutMs,
                command.EventOrdinal,
                command.EventPath,
                command.Cursor,
                command.Limit)),
        new(
            new(
                "trace.range-counters",
                SchemaVersion.V1,
                "writesLocalArtifacts",
                true,
                "implemented",
                "Exports exact raw counter values by exported range name.",
                "trace.range-counters <trace> (--event-ordinal N|--event-path P) " +
                "--counter <exact> [--counter <exact>...] [--range <exact>...] " +
                "[--cursor N] [--limit N]",
                Parameters: [OperationParameters.Trace, OperationParameters.EventOrdinal, OperationParameters.EventPath, new("--counter", "string", true, "Exact counter column name. Repeatable.", Repeatable: true), new("--range", "string", false, "Exact exported range name. Repeatable; omit for every range.", Repeatable: true), OperationParameters.Cursor, OperationParameters.Limit, OperationParameters.Viewer, OperationParameters.TimeoutMs, OperationParameters.Compact]),
            command => TraceRangeCountersOperation.ExecuteAsync(
                command.TracePath!,
                command.ViewerPath,
                command.TimeoutMs,
                command.EventOrdinal,
                command.EventPath,
                command.CounterNames,
                command.RangeNames,
                command.Cursor,
                command.Limit)),
        new(
            new(
                "find-ranges",
                SchemaVersion.V1,
                "readOnly",
                true,
                "implemented",
                "Returns bounded range candidates ordered by Viewer duration, with exact EventKeys and shared ancestor context.",
                "find-ranges <trace> [--name-contains S] [--grain marker|container|all] " +
                "[--within-event-ordinal N] [--cursor N] [--limit N] [--viewer <path>]",
                "wrapper",
                Parameters: [OperationParameters.Trace, OperationParameters.RangeNameContains, new("--grain", "string", false, "Which range kind to consider.", Default: "all", AllowedValues: ["marker", "container", "all"]), new("--within-event-ordinal", "integer", false, "Restrict candidates to strict descendants of this exact ancestor ordinal.", Minimum: 0), OperationParameters.Cursor, OperationParameters.Limit, OperationParameters.Viewer, OperationParameters.TimeoutMs, OperationParameters.Compact]),
            command => FindRangesWrapper.ExecuteAsync(
                command.TracePath!,
                command.ViewerPath,
                command.TimeoutMs,
                command.RangeNameContains,
                command.Grain,
                command.WithinEventOrdinal,
                command.Cursor,
                command.Limit)),
        new(
            new(
                "resolve-event",
                SchemaVersion.V1,
                "readOnly",
                true,
                "implemented",
                "Resolves one explicit exact-name occurrence to an EventKey by stable preorder scan.",
                "resolve-event <trace> --event-name <exact> --event-occurrence N " +
                "[--event-name-mode exact|contains] [--within-event-ordinal N] " +
                "[--viewer <path>]",
                "wrapper",
                Parameters: [OperationParameters.Trace, new("--event-name", "string", true, "Event description to match. Exact and case-sensitive unless --event-name-mode is contains."), new("--event-name-mode", "string", false, "How --event-name is matched. contains is case-insensitive substring matching and is refused when it spans more than one distinct name.", Default: "exact", AllowedValues: ["exact", "contains"]), new("--event-occurrence", "integer", true, "Zero-based index among events sharing the exact name. The first occurrence is 0; a name matched N times accepts 0..N-1.", Minimum: 0), new("--within-event-ordinal", "integer", false, "Restrict the scan to the subtree of this exact ancestor ordinal.", Minimum: 0), OperationParameters.Viewer, OperationParameters.TimeoutMs, OperationParameters.Compact]),
            command => ResolveEventWrapper.ExecuteAsync(
                command.TracePath!,
                command.ViewerPath,
                command.TimeoutMs,
                command.EventName!,
                command.EventOccurrence!.Value,
                command.WithinEventOrdinal,
                command.EventNameContains)),
        new(
            new(
                "inspect-pass",
                SchemaVersion.V1,
                "readOnly",
                true,
                "implemented",
                "Closes metrics and shader pages for one exact pass/marker; v1 leaves Instruction Mix to its explicit atom.",
                "inspect-pass <trace> --event-ordinal N [--table <exact>...] " +
                "[--top-shaders N] [--viewer <path>]",
                "wrapper",
                Parameters: [OperationParameters.Trace, OperationParameters.RequiredEventOrdinal, OperationParameters.MetricTable, OperationParameters.TopShaders, OperationParameters.Viewer, OperationParameters.TimeoutMs, OperationParameters.Compact]),
            command => InspectPassWrapper.ExecuteAsync(
                command.TracePath!,
                command.ViewerPath,
                command.TimeoutMs,
                command.EventOrdinal!.Value,
                command.MetricTables,
                command.TopShaderCount)),
        new(
            new(
                "compare-ranges",
                SchemaVersion.V1,
                "readOnly",
                true,
                "implemented",
                "Computes exact target-minus-baseline deltas across two inspected ranges.",
                "compare-ranges <target-trace> --event-ordinal N " +
                "[--baseline-trace <trace>] --baseline-event-ordinal N " +
                "[--table <exact>...] [--top-shaders N] " +
                "[--cursor N] [--limit N] [--viewer <path>]",
                "wrapper",
                Parameters: [OperationParameters.Trace, OperationParameters.RequiredEventOrdinal, new("--baseline-trace", "path", false, "Baseline report for a cross-trace comparison.", Default: "the target trace"), new("--baseline-event-ordinal", "integer", true, "Zero-based preorder ordinal of the baseline range.", Minimum: 0), OperationParameters.MetricTable, OperationParameters.TopShaders, OperationParameters.Cursor, OperationParameters.Limit, OperationParameters.Viewer, OperationParameters.TimeoutMs, OperationParameters.Compact]),
            command => CompareRangesWrapper.ExecuteAsync(
                command.TracePath!,
                command.BaselineTracePath ?? command.TracePath!,
                command.ViewerPath,
                command.TimeoutMs,
                command.EventOrdinal!.Value,
                command.BaselineEventOrdinal!.Value,
                command.MetricTables,
                command.TopShaderCount,
                command.Cursor,
                command.Limit)),
        new(
            new(
                "compare-frame-timing",
                SchemaVersion.V1,
                "readOnly",
                true,
                "implemented",
                "Aligns two explicit same-trace frame events with Trace Analysis and Present timing, then computes exact display-based deltas.",
                "compare-frame-timing <trace> --event-ordinal N --target-frame-index N " +
                "--baseline-event-ordinal N --baseline-frame-index N " +
                "--analysis-seed-event-ordinal N --present-queue-event-ordinal N " +
                "[--viewer <path>]",
                "wrapper",
                Parameters: [OperationParameters.Trace, OperationParameters.RequiredEventOrdinal, new("--target-frame-index", "integer", true, "Zero-based Trace Analysis frame index of the target event.", Minimum: 0), new("--baseline-event-ordinal", "integer", true, "Zero-based preorder ordinal of the baseline frame event.", Minimum: 0), new("--baseline-frame-index", "integer", true, "Zero-based Trace Analysis frame index of the baseline event.", Minimum: 0), new("--analysis-seed-event-ordinal", "integer", true, "Ordinal of a real pass/marker used to seed Trace Analysis.", Minimum: 0), new("--present-queue-event-ordinal", "integer", true, "Ordinal of the queue event containing Present commands.", Minimum: 0), OperationParameters.Viewer, OperationParameters.TimeoutMs, OperationParameters.Compact]),
            command => CompareFrameTimingWrapper.ExecuteAsync(
                command.TracePath!,
                command.ViewerPath,
                command.TimeoutMs,
                command.EventOrdinal!.Value,
                command.TargetFrameIndex!.Value,
                command.BaselineEventOrdinal!.Value,
                command.BaselineFrameIndex!.Value,
                command.AnalysisSeedEventOrdinal!.Value,
                command.PresentQueueEventOrdinal!.Value)),
        new(
            new(
                "viewer-session.close",
                SchemaVersion.V1,
                "transport",
                false,
                "implemented",
                "Closes the reusable Viewer transport for one exact trace snapshot.",
                "viewer-session.close <trace> [--viewer <path>]",
                "transport",
                Parameters: [OperationParameters.Trace, OperationParameters.Viewer, OperationParameters.TimeoutMs, OperationParameters.Compact]),
            command => ViewerSessionCloseOperation.ExecuteAsync(
                command.TracePath!,
                command.ViewerPath,
                command.TimeoutMs),
            Discoverable: false),
    ];

    public static bool IsKnown(string operation) =>
        Definitions.Any(definition =>
            definition.Descriptor.Id.Equals(operation, StringComparison.Ordinal));

    public static OperationResult Describe() =>
        CapabilitiesOperation.Execute(GetPublicDescriptors());

    public static Task<OperationResult> ExecuteAsync(ParsedCommand command) =>
        Definitions.Single(definition =>
            definition.Descriptor.Id.Equals(
                command.Operation, StringComparison.Ordinal)).Handler(command);

    private static IReadOnlyList<OperationDescriptor> GetPublicDescriptors() =>
        Definitions
            .Where(definition => definition.Discoverable)
            .Select(definition => definition.Descriptor)
            .ToArray();
}
