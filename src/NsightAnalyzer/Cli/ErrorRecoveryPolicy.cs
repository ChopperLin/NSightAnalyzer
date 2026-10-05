using NsightAnalyzer.Contracts;

namespace NsightAnalyzer.Cli;

internal static class ErrorRecoveryPolicy
{
    public static ErrorRecovery For(string operation, OperationError error)
    {
        if (error.Code == "runtime.response_limit_exceeded")
        {
            if (operation is "inspect-pass" or "compare-ranges")
                return new(false, "reducePageOrShaderCount", operation);
            return operation is "find-metrics" or "find-source-hotspots" or "compare-timings" ||
                operation.StartsWith("trace.") && operation is not
                    ("trace.info" or "trace.event-parameters" or "trace.shader-profile")
                ? new(false, "reducePageSize", operation, "--limit")
                : new(false, "requestPagedFacts", "trace.range-shaders");
        }
        if (error.Code == "cli.operation_unknown")
            return new(false, "discoverOperations", "capabilities");
        if (error.Code == "wrapper.metric_not_found")
            return new(false, "discoverMetricNames", "trace.range-metric-catalog");
        if (error.Code == "wrapper.source_view_not_found")
            return new(false, "verifySourceView", "find-source-hotspots", "--view");
        if (error.Code is "wrapper.source_view_unavailable" or "wrapper.source_samples_unavailable")
            return new(false, "useShaderProfile", "trace.shader-profile");
        if (error.Code == "wrapper.shader_comparison_ambiguous")
            return new(false, "inspectShaderIdentities", "trace.range-shaders");
        if (error.Code == "trace.unsupported_draw_scope")
        {
            return new(false, "selectPassOrMarker", "find-ranges");
        }
        if (error.Code is "viewer.bridge_not_installed" or "viewer.not_found" ||
            error.Category == ErrorCategory.AdapterMismatch && error.Code != "viewer.projection_mismatch")
        {
            return new(false, "checkPinnedInstallation", "doctor");
        }
        if (error.Code == "viewer.projection_mismatch")
        {
            return new(false, "inspectLocalDiagnostics");
        }
        if (error.Code == "trace.shader_source_identity_ambiguous")
        {
            return new(false, "useShaderProfile", "trace.shader-profile");
        }
        if (error.Code.Contains("ambiguous", StringComparison.Ordinal))
        {
            return new(false, "resolveExactScope", "find-events");
        }
        return error.Category switch
        {
            ErrorCategory.InvalidInput => new(false, "describeOperation",
                OperationRegistry.IsKnown(operation) ? operation : "capabilities"),
            ErrorCategory.NotFound => new(false, "verifyRequestedIdentity"),
            ErrorCategory.Unsupported or ErrorCategory.Unavailable =>
                new(false, "useAvailableEvidence"),
            ErrorCategory.Timeout => new(false, "narrowRequest", operation),
            ErrorCategory.Viewer => new(true, "retryOnce"),
            _ => new(false, "inspectLocalDiagnostics"),
        };
    }
}
