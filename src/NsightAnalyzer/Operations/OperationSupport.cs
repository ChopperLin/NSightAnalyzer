using NsightAnalyzer.Contracts;

namespace NsightAnalyzer.Operations;

internal static class OperationSupport
{
    public static IReadOnlyList<OperationWarning> ViewerWarnings =>
    [
        new(
            "viewer.unsupported_version_pinned",
            "Facts were decoded through an unsupported, version-pinned Nsight Viewer model adapter."),
    ];

    public static OperationResult ProjectionFailure(Exception exception) =>
        OperationResult.Failure(
            ErrorCategory.AdapterMismatch,
            "viewer.projection_mismatch",
            "The decoded Viewer model does not match the pinned semantic projection.",
            exception.Message.Length <= 512
                ? exception.Message
                : exception.Message[..512]);
}
