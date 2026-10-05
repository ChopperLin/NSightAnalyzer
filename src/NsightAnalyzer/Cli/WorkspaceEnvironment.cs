namespace NsightAnalyzer.Cli;

/// <summary>The CLI invocation owns these process-local overrides, never machine/user settings.</summary>
internal sealed class WorkspaceEnvironment : IDisposable
{
    private const string RunVariable = "NSIGHT_ANALYZER_RUN_ROOT";
    private const string SessionVariable = "NSIGHT_ANALYZER_SESSION_ROOT";
    private readonly string? previousRunRoot;
    private readonly string? previousSessionRoot;
    private readonly bool changed;

    private WorkspaceEnvironment(string? workspace)
    {
        if (workspace is null) return;
        previousRunRoot = Environment.GetEnvironmentVariable(RunVariable);
        previousSessionRoot = Environment.GetEnvironmentVariable(SessionVariable);
        Environment.SetEnvironmentVariable(RunVariable, Path.Combine(workspace, ".local", "runs"));
        Environment.SetEnvironmentVariable(SessionVariable, Path.Combine(workspace, ".local", "sessions"));
        changed = true;
    }

    public static WorkspaceEnvironment Apply(string? workspace) => new(workspace);

    public void Dispose()
    {
        if (!changed) return;
        Environment.SetEnvironmentVariable(RunVariable, previousRunRoot);
        Environment.SetEnvironmentVariable(SessionVariable, previousSessionRoot);
    }
}
