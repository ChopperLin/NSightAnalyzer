using NsightAnalyzer.Adapters.NsightViewer2026_2;
using NsightAnalyzer.Contracts;

namespace NsightAnalyzer.Operations;

internal static class TraceShaderProfileOperation
{
    public static async Task<OperationResult> ExecuteAsync(
        string tracePath,
        string? viewerPath,
        int timeoutMs,
        int? eventOrdinal,
        string? eventPath,
        string shaderStage,
        string shaderHash,
        int shaderOccurrence)
    {
        var opened = await TraceArtifactReader.OpenAsync(
            tracePath, "localWeak", timeoutMs);
        if (!opened.IsSuccess)
        {
            return OperationResult.Failure(opened.Error!);
        }
        var artifact = opened.Artifact!;
        var settings = TraceRangeShadersOperation.BridgeSettings(
            eventOrdinal, eventPath);
        var probe = await ViewerProbeRunner.RunAsync(
            artifact,
            ViewerProbeMode.SelectionMetricsExport,
            "NsightSolidProbeSelectionMetricsV1",
            settings,
            viewerPath,
            timeoutMs,
            ViewerProbeCachePolicy.RangeShadersV1);
        if (!probe.IsSuccess)
        {
            return OperationResult.Failure(probe.Error!);
        }

        using var run = probe.Run!;
        try
        {
            var value = BridgeProjection.ProjectShaderProfile(
                run.Document.RootElement,
                eventOrdinal,
                eventPath,
                shaderStage,
                shaderHash,
                shaderOccurrence);
            return OperationResult.Success(
                value,
                [ViewerProbeRunner.CreateProvenance(
                    run, artifact, "trace.shader-profile/v1")],
                OperationSupport.ViewerWarnings);
        }
        catch (BridgeFactNotFoundException exception)
        {
            return OperationResult.Failure(
                ErrorCategory.NotFound,
                exception.Code,
                exception.Message);
        }
        catch (BridgeScopeUnsupportedException exception)
        {
            return OperationSupport.ScopeUnsupportedFailure(exception);
        }
        catch (BridgeSchemaException exception)
        {
            return OperationSupport.ProjectionFailure(exception);
        }
    }
}
