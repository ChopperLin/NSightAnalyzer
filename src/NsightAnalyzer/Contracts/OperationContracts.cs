using System.Text.Json.Serialization;

namespace NsightAnalyzer.Contracts;

public sealed record SchemaVersion(int Major, int Minor)
{
    public static readonly SchemaVersion V1 = new(1, 0);
    public static readonly SchemaVersion V2 = new(2, 1);
}

public sealed record OperationEnvelope(
    string Operation,
    SchemaVersion SchemaVersion,
    OperationResult Result);

public sealed record OperationResult(
    bool IsSuccess,
    object? Value = null,
    IReadOnlyList<FactProvenance>? Provenance = null,
    IReadOnlyList<OperationWarning>? Warnings = null,
    OperationError? Error = null)
{
    public static OperationResult Success(
        object value,
        IReadOnlyList<FactProvenance>? provenance = null,
        IReadOnlyList<OperationWarning>? warnings = null) =>
        new(true, value, provenance ?? [], warnings ?? []);

    public static OperationResult Failure(OperationError error) =>
        new(false, Error: error);

    public static OperationResult Failure(
        ErrorCategory category,
        string code,
        string message,
        string? detail = null) =>
        new(false, Error: new(category, code, message, detail));
}

[JsonConverter(typeof(JsonStringEnumConverter<ErrorCategory>))]
public enum ErrorCategory
{
    InvalidInput,
    NotFound,
    Unsupported,
    Unavailable,
    AdapterMismatch,
    Trace,
    Viewer,
    Timeout,
    Internal,
}

public sealed record OperationError(
    ErrorCategory Category,
    string Code,
    string Message,
    string? Detail = null,
    ErrorRecovery? Recovery = null);

public sealed record ErrorRecovery(
    bool Retryable,
    string Action,
    string? Operation = null,
    string? Parameter = null);

public sealed record OperationWarning(string Code, string Message);

public sealed record FactProvenance(
    string Source,
    string SupportLevel,
    string ProducerVersion,
    string ProducerBuild,
    string ProducerSku,
    string QtVersion,
    string BridgeVersion,
    string ArtifactIdentity,
    string SourceSchema,
    string Projection,
    string Mode);

public sealed record Page<T>(
    IReadOnlyList<T> Items,
    int Cursor,
    int Limit,
    int TotalCount,
    int ReturnedCount,
    bool Truncated,
    int? NextCursor);

/// <summary>
/// Machine-readable definition of one operation option. Callers should be able
/// to construct a valid invocation from this without parsing the usage string
/// or discovering a convention by trial and error.
/// </summary>
public sealed record OperationParameter(
    string Name,
    string ValueKind,
    bool Required,
    string Description,
    string? Default = null,
    IReadOnlyList<string>? AllowedValues = null,
    long? Minimum = null,
    long? Maximum = null,
    bool Repeatable = false,
    string? ExclusiveGroup = null);

public sealed record OperationDescriptor(
    string Id,
    SchemaVersion SchemaVersion,
    string Effect,
    bool OpensViewer,
    string Maturity,
    string Description,
    string Invocation,
    string Layer = "atom",
    IReadOnlyList<OperationParameter>? Parameters = null,
    IReadOnlyList<OperationOptionConstraint>? Constraints = null);

public static class ContractLimits
{
    public const int MaximumResponseBytes = 1024 * 1024;
    public const int MaximumTimeoutMs = 600_000;
    public const int MaximumPageLimit = 500;
    public const int DefaultPageLimit = 20;
    public const int DefaultTopShaders = 5;

    /// <summary>
    /// Page bound for shader facts. One shader row carries its instruction mix
    /// and stall reasons, so it is an order of magnitude larger than any other
    /// row: a 500-row page of the widest observed rows exceeds
    /// <see cref="MaximumResponseBytes"/> and fails after the work is done.
    /// Publishing the bound that can actually be served keeps the schema
    /// honest, at the cost of more pages.
    /// <para>
    /// Measured, not estimated: on a real range, 175 rows render to 991 KB and
    /// 200 rows exceed the bound. 150 is the largest round page that keeps
    /// meaningful headroom for a range whose rows are wider than that one's.
    /// </para>
    /// </summary>
    public const int MaximumShaderPageLimit = 150;
    // Event descriptions can contain expanded API structs and arrays. Keep
    // discovery pages compact enough that a successful decoder request cannot
    // fail only while serializing its result.
    public const int MaximumEventSearchPageLimit = 50;
    public const int MaximumMetricTableFilters = 32;
    public const int MaximumCounterFilters = 32;
    public const int MaximumRangeFilters = 32;
}

public static class ExitCodes
{
    public const int Success = 0;
    public const int InvalidInput = 2;
    public const int NotFound = 3;
    public const int Unsupported = 4;
    public const int Unavailable = 5;
    public const int AdapterMismatch = 6;
    public const int Trace = 7;
    public const int Viewer = 8;
    public const int Timeout = 9;
    public const int Internal = 10;

    public static int From(ErrorCategory category) => category switch
    {
        ErrorCategory.InvalidInput => InvalidInput,
        ErrorCategory.NotFound => NotFound,
        ErrorCategory.Unsupported => Unsupported,
        ErrorCategory.Unavailable => Unavailable,
        ErrorCategory.AdapterMismatch => AdapterMismatch,
        ErrorCategory.Trace => Trace,
        ErrorCategory.Viewer => Viewer,
        ErrorCategory.Timeout => Timeout,
        _ => Internal,
    };
}
