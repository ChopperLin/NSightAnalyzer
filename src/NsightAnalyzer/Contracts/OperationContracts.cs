using System.Text.Json.Serialization;

namespace NsightAnalyzer.Contracts;

public sealed record SchemaVersion(int Major, int Minor)
{
    public static readonly SchemaVersion V1 = new(1, 0);
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
    string? Detail = null);

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

public sealed record OperationDescriptor(
    string Id,
    SchemaVersion SchemaVersion,
    string Effect,
    bool OpensViewer,
    string Maturity,
    string Description,
    string Invocation);

public static class ContractLimits
{
    public const int MaximumResponseBytes = 1024 * 1024;
    public const int MaximumTimeoutMs = 600_000;
    public const int MaximumPageLimit = 500;
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
