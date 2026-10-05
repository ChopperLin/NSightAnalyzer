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
                "version", SchemaVersion.V2, "readOnly", false, "verified",
                "Returns this CLI build, revision and content fingerprint without opening the Viewer.",
                "version", "discovery", Parameters: [OperationParameters.Pretty]),
            _ => Task.FromResult(VersionOperation.Execute())),
        new(
            new(
                "doctor", SchemaVersion.V2, "readOnly", false, "implemented",
                "Checks local dependencies and workspace paths without launching Viewer; runtime decoder validation remains pending.",
                "doctor [--viewer <path>]", "discovery",
                Parameters: [OperationParameters.Viewer, OperationParameters.Pretty]),
            command => Task.FromResult(DoctorOperation.Execute(command.ViewerPath))),
        new(
            new(
                "describe", SchemaVersion.V2, "readOnly", false, "verified",
                "Returns parameters and defaults for one operation without opening the Viewer.",
                "describe <operation>", "discovery",
                Parameters: [new("<operation>", "string", true, "Operation id from capabilities."), OperationParameters.Pretty]),
            command => Task.FromResult(DescribeOperation(command.DescribedOperation!))),
        new(
            new(
                "capabilities",
                SchemaVersion.V2,
                "readOnly",
                false,
                "verified",
                "Returns the currently callable GPU Trace atom and wrapper catalog.",
                "capabilities [--detail]",
                Parameters: [OperationParameters.Detail, OperationParameters.Pretty]),
            command => Task.FromResult(
                CapabilitiesOperation.Execute(GetPublicDescriptors(), command.Detail))),
        new(
            new(
                "trace.info",
                SchemaVersion.V2,
                "readOnly",
                true,
                "implemented",
                "Returns trace identity and verifies the pinned Viewer decoder/bridge.",
                "trace.info <trace> [--identity-mode localWeak|sha256] [--viewer <path>]",
                Parameters: [OperationParameters.Trace, new("--identity-mode", "string", false, "How the trace snapshot is identified.", Default: "localWeak", AllowedValues: ["localWeak", "sha256"]), OperationParameters.Viewer, OperationParameters.TimeoutMs, OperationParameters.Pretty]),
            command => TraceInfoOperation.ExecuteAsync(
                command.TracePath!,
                command.ViewerPath,
                command.TimeoutMs,
                command.IdentityMode)),
        new(
            new(
                "trace.events",
                SchemaVersion.V2,
                "readOnly",
                true,
                "implemented",
                "Returns a bounded page of semantic Event List facts and exact EventKeys.",
                "trace.events <trace> [--cursor N] [--limit N] [--viewer <path>]",
                Parameters: [OperationParameters.Trace, OperationParameters.Cursor, OperationParameters.Limit, OperationParameters.Viewer, OperationParameters.TimeoutMs, OperationParameters.Pretty]),
            command => TraceEventsOperation.ExecuteAsync(
                command.TracePath!,
                command.ViewerPath,
                command.TimeoutMs,
                command.Cursor,
                command.Limit)),
        new(
            new(
                "trace.outline",
                SchemaVersion.V2,
                "readOnly",
                true,
                "implemented",
                "Returns the range-grain skeleton: every pass/marker and command-list range with its timing, without per-draw noise.",
                "trace.outline <trace> [--grain marker|container|all] [--cursor N] [--limit N] [--viewer <path>]",
                Parameters: [OperationParameters.Trace, new("--grain", "string", false, "Which range kind to return. 'marker' is the caller's own instrumentation; 'container' is the D3D12 objects and calls that structure the capture. Filtered in the decoder, so a narrower grain costs fewer pages.", Default: "all", AllowedValues: ["marker", "container", "all"]), OperationParameters.Cursor, OperationParameters.Limit, OperationParameters.Viewer, OperationParameters.TimeoutMs, OperationParameters.Pretty]),
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
                SchemaVersion.V2,
                "readOnly",
                true,
                "implemented",
                "Returns structured parameters for one exact EventKey.",
                "trace.event-parameters <trace> (--event-ordinal N|--event-path P)",
                Parameters: [OperationParameters.Trace, OperationParameters.EventOrdinal, OperationParameters.EventPath, OperationParameters.Viewer, OperationParameters.TimeoutMs, OperationParameters.Pretty]),
            command => TraceEventParametersOperation.ExecuteAsync(
                command.TracePath!,
                command.ViewerPath,
                command.TimeoutMs,
                command.EventOrdinal,
                command.EventPath)),
        new(
            new(
                "trace.range-metric-catalog",
                SchemaVersion.V2,
                "readOnly",
                true,
                "implemented",
                "Returns compact Warp Metrics table and column identities for one exact pass/marker range.",
                "trace.range-metric-catalog <trace> (--event-ordinal N|--event-path P) " +
                "[--cursor N] [--limit N]",
                Parameters: [OperationParameters.Trace, OperationParameters.EventOrdinal, OperationParameters.EventPath, OperationParameters.Cursor, OperationParameters.Limit, OperationParameters.Viewer, OperationParameters.TimeoutMs, OperationParameters.Pretty]),
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
                SchemaVersion.V2,
                "readOnly",
                true,
                "implemented",
                "Returns long-form Warp Metrics for one exact pass/marker EventKey.",
                "trace.range-metrics <trace> (--event-ordinal N|--event-path P) " +
                "[--table <exact>] [--cursor N] [--limit N]",
                Parameters: [OperationParameters.Trace, OperationParameters.EventOrdinal, OperationParameters.EventPath, OperationParameters.MetricTable, OperationParameters.Detail, OperationParameters.Cursor, OperationParameters.Limit, OperationParameters.Viewer, OperationParameters.TimeoutMs, OperationParameters.Pretty]),
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
                SchemaVersion.V2,
                "readOnly",
                true,
                "implemented",
                "Returns paged static/dynamic shader facts for one exact range.",
                "trace.range-shaders <trace> (--event-ordinal N|--event-path P) " +
                "[--shader-hash H] [--shader-occurrence N] [--cursor N] [--limit N]",
                Parameters: [OperationParameters.Trace, OperationParameters.EventOrdinal, OperationParameters.EventPath, OperationParameters.ShaderHash, OperationParameters.ShaderOccurrence with { Default = null, Description = "Zero-based occurrence within each stage/hash; requires --shader-hash. Omit to return every matching occurrence." }, OperationParameters.Detail, OperationParameters.Cursor, OperationParameters.ShaderLimit, OperationParameters.Viewer, OperationParameters.TimeoutMs, OperationParameters.Pretty]),
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
                SchemaVersion.V2,
                "readOnly",
                true,
                "implemented",
                "Returns one exact shader's static, sampled, instruction and stall profile.",
                "trace.shader-profile <trace> (--event-ordinal N|--event-path P) " +
                "--shader-stage S --shader-hash H [--shader-occurrence N]",
                Parameters: [OperationParameters.Trace, OperationParameters.EventOrdinal, OperationParameters.EventPath, OperationParameters.ShaderStage, OperationParameters.RequiredShaderHash, OperationParameters.ShaderOccurrence, OperationParameters.Viewer, OperationParameters.TimeoutMs, OperationParameters.Pretty]),
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
                SchemaVersion.V2,
                "readOnly",
                true,
                "implemented",
                "Returns dynamic range-level instruction categories, samples, and stalls.",
                "trace.range-instruction-mix <trace> " +
                "(--event-ordinal N|--event-path P) [--cursor N] [--limit N]",
                Parameters: [OperationParameters.Trace, OperationParameters.EventOrdinal, OperationParameters.EventPath, OperationParameters.Cursor, OperationParameters.Limit, OperationParameters.Viewer, OperationParameters.TimeoutMs, OperationParameters.Pretty]),
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
                SchemaVersion.V2,
                "readOnly",
                true,
                "implemented",
                "Returns paged DXIL/SASS-correlated hotspot rows for one exact shader.",
                "trace.shader-source <trace> (--event-ordinal N|--event-path P) " +
                "--shader-stage S --shader-hash H [--shader-occurrence N] [--cursor N] [--limit N]",
                Parameters: [OperationParameters.Trace, OperationParameters.EventOrdinal, OperationParameters.EventPath, OperationParameters.ShaderStage, OperationParameters.RequiredShaderHash, OperationParameters.ShaderOccurrence, OperationParameters.Cursor, OperationParameters.Limit, OperationParameters.Viewer, OperationParameters.TimeoutMs, OperationParameters.Pretty]),
            command => TraceShaderSourceOperation.ExecuteAsync(
                command.TracePath!,
                command.ViewerPath,
                command.TimeoutMs,
                command.EventOrdinal,
                command.EventPath,
                command.ShaderStage!,
                command.ShaderHash!,
                command.ShaderOccurrence ?? 0,
                command.Cursor,
                command.Limit)),
        new(
            new(
                "trace.analysis",
                SchemaVersion.V2,
                "readOnly",
                true,
                "implemented",
                "Returns Viewer Trace Analysis ranges, top issues, and annotations.",
                "trace.analysis <trace> (--event-ordinal N|--event-path P) " +
                "[--cursor N] [--limit N] [--viewer <path>]",
                Parameters: [OperationParameters.Trace, OperationParameters.EventOrdinal, OperationParameters.EventPath, OperationParameters.Cursor, OperationParameters.Limit, OperationParameters.Viewer, OperationParameters.TimeoutMs, OperationParameters.Pretty]),
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
                SchemaVersion.V2,
                "writesLocalArtifacts",
                true,
                "implemented",
                "Exports and returns the raw GPU counter column catalog.",
                "trace.counter-catalog <trace> (--event-ordinal N|--event-path P) " +
                "[--cursor N] [--limit N] [--viewer <path>]",
                Parameters: [OperationParameters.Trace, OperationParameters.EventOrdinal, OperationParameters.EventPath, OperationParameters.Cursor, OperationParameters.Limit, OperationParameters.Viewer, OperationParameters.TimeoutMs, OperationParameters.Pretty]),
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
                SchemaVersion.V2,
                "writesLocalArtifacts",
                true,
                "implemented",
                "Exports exact raw counter values by exported range name.",
                "trace.range-counters <trace> (--event-ordinal N|--event-path P) " +
                "--counter <exact> [--counter <exact>...] [--range <exact>...] " +
                "[--cursor N] [--limit N]",
                Parameters: [OperationParameters.Trace, OperationParameters.EventOrdinal, OperationParameters.EventPath, new("--counter", "string", true, "Exact counter column name. Repeatable.", Repeatable: true), new("--range", "string", false, "Exact exported range name. Repeatable; omit for every range.", Repeatable: true), OperationParameters.Cursor, OperationParameters.Limit, OperationParameters.Viewer, OperationParameters.TimeoutMs, OperationParameters.Pretty]),
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
                "find-events",
                SchemaVersion.V2,
                "readOnly",
                true,
                "implemented",
                "Returns bounded matching Event List candidates in preorder, with exact EventKeys and shared ancestor context.",
                "find-events <trace> --name-contains S [--within-event-ordinal N] " +
                "[--cursor N] [--limit N] [--viewer <path>]",
                "wrapper",
                Parameters: [OperationParameters.Trace, OperationParameters.EventNameContains, new("--within-event-ordinal", "integer", false, "Restrict candidates to strict descendants of this exact ancestor ordinal.", Minimum: 0), OperationParameters.Cursor, OperationParameters.EventSearchLimit, OperationParameters.Viewer, OperationParameters.TimeoutMs, OperationParameters.Pretty]),
            command => FindEventsWrapper.ExecuteAsync(
                command.TracePath!,
                command.ViewerPath,
                command.TimeoutMs,
                command.RangeNameContains!,
                command.WithinEventOrdinal,
                command.Cursor,
                command.Limit)),
        new(
            new(
                "find-ranges",
                SchemaVersion.V2,
                "readOnly",
                true,
                "implemented",
                "Returns bounded range candidates ordered by Viewer duration, with exact EventKeys and shared ancestor context.",
                "find-ranges <trace> [--name-contains S] [--grain marker|container|all] " +
                "[--within-event-ordinal N] [--cursor N] [--limit N] [--viewer <path>]",
                "wrapper",
                Parameters: [OperationParameters.Trace, OperationParameters.RangeNameContains, new("--grain", "string", false, "Which range kind to consider.", Default: "marker", AllowedValues: ["marker", "container", "all"]), new("--within-event-ordinal", "integer", false, "Restrict candidates to strict descendants of this exact ancestor ordinal.", Minimum: 0), OperationParameters.Cursor, OperationParameters.Limit, OperationParameters.Viewer, OperationParameters.TimeoutMs, OperationParameters.Pretty]),
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
                SchemaVersion.V2,
                "readOnly",
                true,
                "implemented",
                "Resolves one explicit exact-name occurrence to an EventKey by stable preorder scan.",
                "resolve-event <trace> --event-name <exact> --event-occurrence N " +
                "[--event-name-mode exact|contains] [--within-event-ordinal N] " +
                "[--viewer <path>]",
                "wrapper",
                Parameters: [OperationParameters.Trace, new("--event-name", "string", true, "Event description to match. Exact and case-sensitive unless --event-name-mode is contains."), new("--event-name-mode", "string", false, "How --event-name is matched. contains is case-insensitive substring matching and is refused when it spans more than one distinct name.", Default: "exact", AllowedValues: ["exact", "contains"]), new("--event-occurrence", "integer", true, "Zero-based index among events sharing the exact name. The first occurrence is 0; a name matched N times accepts 0..N-1.", Minimum: 0), new("--within-event-ordinal", "integer", false, "Restrict the scan to the subtree of this exact ancestor ordinal.", Minimum: 0), OperationParameters.Viewer, OperationParameters.TimeoutMs, OperationParameters.Pretty]),
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
                "find-metrics", SchemaVersion.V2, "readOnly", true, "implemented",
                "Returns a bounded metric page matching an explicit name substring or exact source ordinal within one range.",
                "find-metrics <trace> --event-ordinal N (--name-contains S|--source-ordinal N) " +
                "[--table <exact>...] [--cursor N] [--limit N] [--detail]",
                "wrapper",
                Parameters: [OperationParameters.Trace, OperationParameters.RequiredEventOrdinal,
                    OperationParameters.MetricNameContains, OperationParameters.MetricSourceOrdinal,
                    OperationParameters.MetricTable, OperationParameters.Detail, OperationParameters.Cursor,
                    OperationParameters.Limit, OperationParameters.Viewer, OperationParameters.TimeoutMs,
                    OperationParameters.Pretty]),
            command => FindMetricsWrapper.ExecuteAsync(
                command.TracePath!, command.ViewerPath, command.TimeoutMs, command.EventOrdinal!.Value,
                command.MetricTables, command.RangeNameContains, command.MetricSourceOrdinal,
                command.Cursor, command.Limit)),
        new(
            new(
                "find-source-hotspots", SchemaVersion.V2, "readOnly", true, "implemented",
                "Ranks sampled source rows within one explicit shader source view, preserving exact row identities and view-local coverage.",
                "find-source-hotspots <trace> (--event-ordinal N|--event-path P) " +
                "--shader-stage S --shader-hash H [--shader-occurrence N] " +
                "--view dxil|sass|hlsl|source [--view-occurrence N] [--cursor N] [--limit N]",
                "wrapper",
                Parameters: [OperationParameters.Trace, OperationParameters.EventOrdinal, OperationParameters.EventPath,
                    OperationParameters.ShaderStage, OperationParameters.RequiredShaderHash, OperationParameters.ShaderOccurrence,
                    OperationParameters.SourceView, OperationParameters.SourceViewOccurrence,
                    OperationParameters.Cursor, OperationParameters.Limit, OperationParameters.Viewer,
                    OperationParameters.TimeoutMs, OperationParameters.Pretty]),
            command => SourceHotspotsWrapper.ExecuteAsync(
                command.TracePath!, command.ViewerPath, command.TimeoutMs, command.EventOrdinal, command.EventPath,
                command.ShaderStage!, command.ShaderHash!, command.ShaderOccurrence ?? 0,
                command.SourceView!, command.SourceViewOccurrence, command.Cursor, command.Limit)),
        new(
            new(
                "inspect-pass",
                SchemaVersion.V2,
                "readOnly",
                true,
                "implemented",
                "Returns a metric page and shader Top-N for one exact pass/marker; optional sections are read only when requested.",
                "inspect-pass <trace> --event-ordinal N [--sections timing|metrics|shaders|instruction-mix[,..]] " +
                "[--table <exact>...] [--top-shaders N] [--cursor N] [--limit N] [--detail] [--viewer <path>]",
                "wrapper",
                Parameters: [OperationParameters.Trace, OperationParameters.RequiredEventOrdinal, OperationParameters.MetricTable, OperationParameters.TopShaders, OperationParameters.InspectSections, OperationParameters.Detail, OperationParameters.Cursor, OperationParameters.Limit, OperationParameters.Viewer, OperationParameters.TimeoutMs, OperationParameters.Pretty]),
            command => InspectPassWrapper.ExecuteAsync(
                command.TracePath!,
                command.ViewerPath,
                command.TimeoutMs,
                command.EventOrdinal!.Value,
                command.MetricTables,
                command.TopShaderCount,
                command.Cursor,
                command.Limit,
                command.Sections)),
        new(
            new(
                "compare-ranges",
                SchemaVersion.V2,
                "readOnly",
                true,
                "implemented",
                "Computes exact target-minus-baseline deltas across two inspected ranges.",
                "compare-ranges <target-trace> --event-ordinal N " +
                "[--baseline-trace <trace>] --baseline-event-ordinal N " +
                "[--sections timing|metrics|shaders|instruction-mix[,..]] [--table <exact>...] " +
                "[--top-shaders N (requires shaders section)] [--include-unchanged] " +
                "[--cursor N] [--limit N] [--detail] [--viewer <path>]",
                "wrapper",
                Parameters: [OperationParameters.Trace, OperationParameters.RequiredEventOrdinal, new("--baseline-trace", "path", false, "Baseline report for a cross-trace comparison.", Default: "the target trace"), new("--baseline-event-ordinal", "integer", true, "Zero-based preorder ordinal of the baseline range.", Minimum: 0), OperationParameters.MetricTable, OperationParameters.TopShaders, OperationParameters.Sections, new("--include-unchanged", "flag", false, "Include unchanged metric deltas; the default delta page contains changed and unmatched facts only."), OperationParameters.Detail, OperationParameters.Cursor, OperationParameters.Limit, OperationParameters.Viewer, OperationParameters.TimeoutMs, OperationParameters.Pretty]),
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
                command.Limit,
                command.Sections,
                command.IncludeUnchanged)),
        new(
            new(
                "compare-timings", SchemaVersion.V2, "readOnly", true, "implemented",
                "Compares only the current page of explicit target/baseline range pairs. Summaries cover that page, grouped by exact context or caller-declared sampleGroup.",
                "compare-timings <target-trace> [--baseline-trace <trace>] --pairs-file <json> " +
                "[--cursor N] [--limit N] [--viewer <path>]",
                "wrapper",
                Parameters: [OperationParameters.Trace,
                    new("--baseline-trace", "path", false, "Baseline report for the explicit pairs.", Default: "the target trace"),
                    new("--pairs-file", "path", true,
                        "Local UTF-8 JSON {pairs:[{targetEventOrdinal:N,baselineEventOrdinal:N,label?:string,sampleGroup?:string}]}; 1..32 pairs, at most 64 KiB. label/sampleGroup are caller metadata, at most 120 characters each; sampleGroup must be nonblank. Default summary grouping uses both endpoints' parent paths, object, thread, description and depth. Explicit sampleGroup allows cross-frame grouping but requires matching names/depths."),
                    OperationParameters.Cursor, OperationParameters.Limit, OperationParameters.Viewer,
                    OperationParameters.TimeoutMs, OperationParameters.Pretty]),
            command => BatchTimingWrapper.ExecuteAsync(
                command.TracePath!, command.BaselineTracePath ?? command.TracePath!, command.PairsFile!,
                command.ViewerPath, command.TimeoutMs, command.Cursor, command.Limit)),
        new(
            new(
                "compare-frame-timing",
                SchemaVersion.V2,
                "readOnly",
                true,
                "implemented",
                "Aligns two explicit same-trace frame events with Trace Analysis and Present timing, then computes exact display-based deltas.",
                "compare-frame-timing <trace> --event-ordinal N --target-frame-index N " +
                "--baseline-event-ordinal N --baseline-frame-index N " +
                "--analysis-seed-event-ordinal N --present-queue-event-ordinal N " +
                "[--viewer <path>]",
                "wrapper",
                Parameters: [OperationParameters.Trace, OperationParameters.RequiredEventOrdinal, new("--target-frame-index", "integer", true, "Trace Analysis frame index of the target event; frame zero lacks a preceding Present.", Minimum: 1), new("--baseline-event-ordinal", "integer", true, "Zero-based preorder ordinal of the baseline frame event.", Minimum: 0), new("--baseline-frame-index", "integer", true, "Trace Analysis frame index of the baseline event; frame zero lacks a preceding Present.", Minimum: 1), new("--analysis-seed-event-ordinal", "integer", true, "Ordinal of a real pass/marker used to seed Trace Analysis.", Minimum: 0), new("--present-queue-event-ordinal", "integer", true, "Ordinal of the queue event containing Present commands.", Minimum: 0), OperationParameters.Viewer, OperationParameters.TimeoutMs, OperationParameters.Pretty]),
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
                SchemaVersion.V2,
                "transport",
                false,
                "implemented",
                "Closes the reusable Viewer transport for one exact trace snapshot.",
                "viewer-session.close <trace> [--viewer <path>]",
                "transport",
                Parameters: [OperationParameters.Trace, OperationParameters.Viewer, OperationParameters.TimeoutMs, OperationParameters.Pretty]),
            command => ViewerSessionCloseOperation.ExecuteAsync(
                command.TracePath!,
                command.ViewerPath,
                command.TimeoutMs),
            Discoverable: false),
    ];

    public static bool IsKnown(string operation) =>
        Definitions.Any(definition =>
            definition.Descriptor.Id.Equals(operation, StringComparison.Ordinal));

    public static OperationResult Describe(bool detail = false) =>
        CapabilitiesOperation.Execute(GetPublicDescriptors(), detail);

    public static OperationResult DescribeOperation(string operation)
    {
        var definition = Definitions.FirstOrDefault(item => item.Descriptor.Id == operation);
        return definition is null
            ? OperationResult.Failure(ErrorCategory.InvalidInput, "cli.operation_unknown",
                $"Unknown operation '{operation}'.")
            : OperationResult.Success(PublicDescriptor(definition.Descriptor));
    }

    internal static OperationParameter? ParameterFor(string operation, string name)
    {
        if (name == "--workspace") return OperationParameters.Workspace;
        if (name == "--compact") return OperationParameters.Compact;
        return Definitions.FirstOrDefault(item => item.Descriptor.Id == operation)?
            .Descriptor.Parameters?.FirstOrDefault(item => item.Name == name);
    }

    public static Task<OperationResult> ExecuteAsync(ParsedCommand command) =>
        Definitions.Single(definition =>
            definition.Descriptor.Id.Equals(
                command.Operation, StringComparison.Ordinal)).Handler(command);

    private static IReadOnlyList<OperationDescriptor> GetPublicDescriptors() =>
        Definitions
            .Where(definition => definition.Discoverable)
            .Select(definition => PublicDescriptor(definition.Descriptor))
            .ToArray();

    private static OperationDescriptor PublicDescriptor(OperationDescriptor descriptor)
    {
        var parameters = descriptor.Parameters ?? [];
        var constraints = new List<OperationOptionConstraint>();
        foreach (var group in parameters.Where(item => item.ExclusiveGroup is not null)
                     .GroupBy(item => item.ExclusiveGroup))
        {
            constraints.Add(new("exactlyOneOf", group.Select(item => item.Name).ToArray()));
        }
        if (descriptor.Id == "trace.range-shaders")
        {
            constraints.Add(new("requires", ["--shader-occurrence"], "--shader-hash"));
        }
        if (descriptor.Id is "inspect-pass" or "compare-ranges")
        {
            constraints.Add(new("requiresValue", ["--table", "--cursor", "--limit"], "--sections", "metrics"));
            constraints.Add(new("requiresValue", ["--top-shaders"], "--sections", "shaders"));
            if (descriptor.Id == "compare-ranges")
            {
                constraints.Add(new("requiresValue", ["--include-unchanged"], "--sections", "metrics"));
            }
        }
        return descriptor with
        {
            Invocation = descriptor.Invocation + " [--workspace <absolute-path>]",
            Parameters = [.. parameters, OperationParameters.Workspace],
            Constraints = constraints.Count == 0 ? null : constraints,
        };
    }
}
