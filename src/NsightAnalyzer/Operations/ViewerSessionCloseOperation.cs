using NsightAnalyzer.Adapters.NsightViewer2026_2;
using NsightAnalyzer.Contracts;

namespace NsightAnalyzer.Operations;

internal static class ViewerSessionCloseOperation
{
    public static async Task<OperationResult> ExecuteAsync(
        string tracePath,
        string? viewerPathOverride,
        int timeoutMs)
    {
        var opened = await TraceArtifactReader.OpenAsync(
            tracePath, "localWeak", timeoutMs);
        if (!opened.IsSuccess)
        {
            return OperationResult.Failure(opened.Error!);
        }

        string viewerPath;
        try
        {
            viewerPath = ViewerHostTargets.ResolveViewerPath(viewerPathOverride);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return OperationResult.Failure(
                ErrorCategory.InvalidInput,
                "viewer.path_invalid",
                "The Nsight Viewer path is invalid.",
                exception.GetType().Name);
        }

        var closed = await ViewerSessionTransport.CloseAsync(
            opened.Artifact!, viewerPath, timeoutMs);
        return closed.IsSuccess
            ? OperationResult.Success(closed.Value!)
            : OperationResult.Failure(closed.Error!);
    }
}
