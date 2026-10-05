using NsightAnalyzer.Contracts;

namespace NsightAnalyzer.Cli;

/// <summary>
/// Shared option definitions for the operation catalog. Options that mean the
/// same thing everywhere are declared once here, so `capabilities` cannot drift
/// from the parser.
/// </summary>
internal static class OperationParameters
{
    public const string ScopeGroup = "eventScope";

    public static OperationParameter Trace { get; } = new(
        "<trace>",
        "path",
        true,
        "Path to an existing .ngfx-gputrace report. Positional, first argument.");

    public static OperationParameter Viewer { get; } = new(
        "--viewer",
        "path",
        false,
        "Exact ngfx-ui.exe of the pinned Viewer build.",
        Default: "the installed Nsight Graphics 2026.2 Viewer");

    public static OperationParameter Workspace { get; } = new(
        "--workspace", "path", false,
        "Absolute task workspace. Anchors run artifacts and reusable Viewer sessions under its .local directory across working directories; use the same value when closing the session.",
        Default: "existing NSIGHT_ANALYZER_RUN_ROOT/SESSION_ROOT overrides, otherwise the current working directory");

    public static OperationParameter Compact { get; } = new(
        "--compact", "flag", false,
        "Legacy alias for the default concise JSON formatting; mutually exclusive with --pretty.");

    public static OperationParameter Pretty { get; } = new(
        "--pretty",
        "flag",
        false,
        "Indent JSON for human reading. Default output is already concise JSON.");

    public static OperationParameter Detail { get; } = new(
        "--detail", "flag", false,
        "Include metric descriptions, comparison table summaries and shader instruction vectors; paging still applies. For capabilities, include all parameter definitions.");

    public static OperationParameter Sections { get; } = new(
        "--sections", "string", false,
        "Comma-separated fact families. Only requested families are read; all requested families must succeed. Timing is always returned. Use timing alone to avoid metrics and shader loading.",
        Default: "metrics", AllowedValues: ["timing", "metrics", "shaders", "instruction-mix"]);

    public static OperationParameter InspectSections { get; } = Sections with
    {
        Default = "metrics,shaders",
    };

    public static OperationParameter TimeoutMs { get; } = new(
        "--timeout-ms",
        "integer",
        false,
        "Overall bound for the operation.",
        Default: "180000",
        Minimum: 1,
        Maximum: ContractLimits.MaximumTimeoutMs);

    public static OperationParameter Cursor { get; } = new(
        "--cursor",
        "integer",
        false,
        "Zero-based index of the first item to return. Use the previous page's " +
        "nextCursor to continue.",
        Default: "0",
        Minimum: 0);

    public static OperationParameter Limit { get; } = new(
        "--limit",
        "integer",
        false,
        "Maximum items to return in one page.",
        Default: "20",
        Minimum: 1,
        Maximum: ContractLimits.MaximumPageLimit);

    /// <summary>
    /// The shader page bound is lower than every other family's. Publishing the
    /// general maximum here would promise a page size that fails on serialization
    /// after the Viewer work is already spent.
    /// </summary>
    public static OperationParameter ShaderLimit { get; } = new(
        "--limit",
        "integer",
        false,
        "Maximum shader rows to return in one page. Lower than other families " +
        "because one shader row carries its instruction mix and stall reasons.",
        Default: "20",
        Minimum: 1,
        Maximum: ContractLimits.MaximumShaderPageLimit);

    public static OperationParameter EventSearchLimit { get; } = new(
        "--limit",
        "integer",
        false,
        "Maximum matching events to return. Kept small because an event name " +
        "may contain expanded API structures and arrays.",
        Default: "20",
        Minimum: 1,
        Maximum: ContractLimits.MaximumEventSearchPageLimit);

    public static OperationParameter EventOrdinal { get; } = new(
        "--event-ordinal",
        "integer",
        false,
        "Zero-based preorder ordinal of the target event, as returned by " +
        "trace.events or resolve-event. Exactly one of --event-ordinal or " +
        "--event-path is required.",
        Minimum: 0,
        ExclusiveGroup: ScopeGroup);

    public static OperationParameter EventPath { get; } = new(
        "--event-path",
        "string",
        false,
        "Dotted tree path of the target event, for example 0.2.12.71.0. " +
        "Exactly one of --event-ordinal or --event-path is required.",
        ExclusiveGroup: ScopeGroup);

    public static OperationParameter RequiredEventOrdinal { get; } = new(
        "--event-ordinal",
        "integer",
        true,
        "Zero-based preorder ordinal of the target pass/marker range.",
        Minimum: 0);

    public static OperationParameter MetricTable { get; } = new(
        "--table",
        "string",
        false,
        "Exact Warp Metrics table name. Repeatable; omit for every table.",
        Repeatable: true);

    public static OperationParameter ShaderHash { get; } = new(
        "--shader-hash",
        "string",
        false,
        "Shader hash as 0x followed by 16 hexadecimal digits.");

    public static OperationParameter RequiredShaderHash { get; } = new(
        "--shader-hash",
        "string",
        true,
        "Shader hash as 0x followed by 16 hexadecimal digits.");

    public static OperationParameter ShaderStage { get; } = new(
        "--shader-stage",
        "string",
        true,
        "Exact semantic shader stage returned by trace.range-shaders, for example Pixel or Compute.");

    public static OperationParameter ShaderOccurrence { get; } = new(
        "--shader-occurrence",
        "integer",
        false,
        "Zero-based index among shaders sharing the same stage and hash in this range. " +
        "The first occurrence is 0.",
        Default: "0",
        Minimum: 0);

    public static OperationParameter RangeNameContains { get; } = new(
        "--name-contains",
        "string",
        false,
        "Case-insensitive range-name substring. Omit to consider every selected-grain range.");

    public static OperationParameter EventNameContains { get; } = new(
        "--name-contains",
        "string",
        true,
        "Case-insensitive substring of the exact Event List description or API command name.");

    public static OperationParameter TopShaders { get; } = new(
        "--top-shaders",
        "integer",
        false,
        "Maximum shaders to return, ordered by sample count. The result reports " +
        "sample coverage and explicit truncation. Requires shaders in --sections (explicitly add --sections shaders or metrics,shaders for compare-ranges).",
        Default: "5",
        Minimum: 1,
        Maximum: 100);

    public static OperationParameter SourceView { get; } = new(
        "--view", "string", true,
        "One source view to rank. Coverage and sample totals apply only to this view, never across correlated views.",
        AllowedValues: ["dxil", "sass", "hlsl", "source"]);

    public static OperationParameter SourceViewOccurrence { get; } = new(
        "--view-occurrence", "integer", false,
        "Zero-based occurrence of the requested source view.", Default: "0", Minimum: 0);

    public static OperationParameter MetricNameContains { get; } = new(
        "--name-contains", "string", false,
        "Case-insensitive metric name substring. Exactly one of --name-contains or --source-ordinal is required.",
        ExclusiveGroup: "metricSelector");

    public static OperationParameter MetricSourceOrdinal { get; } = new(
        "--source-ordinal", "integer", false,
        "Exact source ordinal returned for this scope's metric. Exactly one of --name-contains or --source-ordinal is required.",
        Minimum: 0, ExclusiveGroup: "metricSelector");
}
