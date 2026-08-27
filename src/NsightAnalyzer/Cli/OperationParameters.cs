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

    public static OperationParameter Compact { get; } = new(
        "--compact",
        "flag",
        false,
        "Emit the single JSON result without indentation.");

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
        Default: "100",
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
        Default: "100",
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
        "sample coverage and explicit truncation.",
        Default: "32",
        Minimum: 1);
}
