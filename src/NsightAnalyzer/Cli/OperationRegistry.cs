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
                "capabilities [--compact]"),
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
                "trace.info <trace> [--identity-mode localWeak|sha256] [--viewer <path>]"),
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
                "trace.events <trace> [--cursor N] [--limit N] [--viewer <path>]"),
            command => TraceEventsOperation.ExecuteAsync(
                command.TracePath!,
                command.ViewerPath,
                command.TimeoutMs,
                command.Cursor,
                command.Limit)),
        new(
            new(
                "trace.event-parameters",
                SchemaVersion.V1,
                "readOnly",
                true,
                "implemented",
                "Returns structured parameters for one exact EventKey.",
                "trace.event-parameters <trace> (--event-ordinal N|--event-path P)"),
            command => TraceEventParametersOperation.ExecuteAsync(
                command.TracePath!,
                command.ViewerPath,
                command.TimeoutMs,
                command.EventOrdinal,
                command.EventPath)),
        new(
            new(
                "trace.range-metrics",
                SchemaVersion.V1,
                "readOnly",
                true,
                "implemented",
                "Returns long-form Warp Metrics for one exact pass/marker EventKey.",
                "trace.range-metrics <trace> (--event-ordinal N|--event-path P) " +
                "[--table <exact>] [--cursor N] [--limit N]"),
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
                "[--shader-hash H] [--shader-occurrence N] [--cursor N] [--limit N]"),
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
                "trace.range-instruction-mix",
                SchemaVersion.V1,
                "readOnly",
                true,
                "implemented",
                "Returns dynamic range-level instruction categories, samples, and stalls.",
                "trace.range-instruction-mix <trace> " +
                "(--event-ordinal N|--event-path P) [--cursor N] [--limit N]"),
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
                "--shader-hash H [--shader-occurrence N] [--cursor N] [--limit N]"),
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
                "[--cursor N] [--limit N] [--viewer <path>]"),
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
                "[--cursor N] [--limit N] [--viewer <path>]"),
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
                "[--cursor N] [--limit N]"),
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
                "resolve-event",
                SchemaVersion.V1,
                "readOnly",
                true,
                "implemented",
                "Resolves one explicit exact-name occurrence to an EventKey by stable preorder scan.",
                "resolve-event <trace> --event-name <exact> --event-occurrence N " +
                "[--within-event-ordinal N] [--viewer <path>]",
                "wrapper"),
            command => ResolveEventWrapper.ExecuteAsync(
                command.TracePath!,
                command.ViewerPath,
                command.TimeoutMs,
                command.EventName!,
                command.EventOccurrence!.Value,
                command.WithinEventOrdinal)),
        new(
            new(
                "inspect-pass",
                SchemaVersion.V1,
                "readOnly",
                true,
                "implemented",
                "Closes metrics, shader, and instruction pages for one exact pass/marker.",
                "inspect-pass <trace> --event-ordinal N [--table <exact>...] " +
                "[--top-shaders N] [--viewer <path>]",
                "wrapper"),
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
                "wrapper"),
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
                "wrapper"),
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
                "transport"),
            command => ViewerSessionCloseOperation.ExecuteAsync(
                command.TracePath!,
                command.ViewerPath,
                command.TimeoutMs),
            Discoverable: false),
    ];

    public static bool IsKnown(string operation) =>
        Definitions.Any(definition =>
            definition.Descriptor.Id.Equals(operation, StringComparison.Ordinal));

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
