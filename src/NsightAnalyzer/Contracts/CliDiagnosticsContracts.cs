namespace NsightAnalyzer.Contracts;

/// <summary>Conditional input rules exposed by describe, separate from fact contracts.</summary>
public sealed record OperationOptionConstraint(
    string Kind,
    IReadOnlyList<string> Parameters,
    string? RequiredParameter = null,
    string? RequiredValue = null);

public sealed record ToolVersionValue(
    string Product,
    string Build,
    string Revision,
    string ContentFingerprint,
    string FingerprintScope,
    string PackageState);

public sealed record DependencyCheck(
    string Component,
    string State,
    string Expected,
    string? Observed = null,
    string? Path = null,
    string? Note = null);

public sealed record DoctorValue(
    ToolVersionValue Tool,
    bool LocalPrerequisitesPresent,
    string RuntimeValidation,
    string RunRoot,
    string SessionRoot,
    IReadOnlyList<DependencyCheck> Checks,
    string SetupReference);
