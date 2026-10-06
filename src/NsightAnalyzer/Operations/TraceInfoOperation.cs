using NsightAnalyzer.Adapters.NsightViewer2026_2;
using NsightAnalyzer.Contracts;

namespace NsightAnalyzer.Operations;

internal static class TraceInfoOperation
{
    public static async Task<OperationResult> ExecuteAsync(
        string tracePath,
        string? viewerPath,
        int timeoutMs,
        string identityMode)
    {
        var opened = await TraceArtifactReader.OpenAsync(tracePath, identityMode, timeoutMs);
        if (!opened.IsSuccess)
        {
            return OperationResult.Failure(opened.Error!);
        }
        var artifact = opened.Artifact!;

        var probe = await ViewerProbeRunner.RunAsync(
            artifact,
            ViewerProbeMode.Heartbeat,
            "NsightSolidProbeHeartbeatV1",
            new Dictionary<string, string>(),
            viewerPath,
            timeoutMs);
        if (!probe.IsSuccess)
        {
            return OperationResult.Failure(probe.Error!);
        }

        using var run = probe.Run!;
        var root = run.Document.RootElement;
        var target = run.Target;
        var value = new TraceInfoValue(
            artifact.ToContract(),
            new(
                target.AdapterName,
                ViewerProbeRunner.SupportLevel,
                target.ProductVersion,
                target.ProductBuild,
                target.ProductSku,
                root.GetProperty("qtRuntimeVersion").GetString()!,
                root.GetProperty("pluginVersion").GetString()!,
                root.GetProperty("schema").GetString()!),
            [
                "traceIdentity",
                "eventHierarchy",
                "eventParameters",
                "rangeMetrics",
            ]);
        return OperationResult.Success(
            value,
            [ViewerProbeRunner.CreateProvenance(run, artifact, "trace.info/v1")],
            OperationSupport.ViewerWarnings);
    }
}
