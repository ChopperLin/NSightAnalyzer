using System.Globalization;
using NsightAnalyzer.Adapters.NsightViewer2026_2;
using NsightAnalyzer.Contracts;

namespace NsightAnalyzer.Operations;

internal static class TraceOutlineOperation
{
    public static async Task<OperationResult> ExecuteAsync(
        string tracePath,
        string? viewerPath,
        int timeoutMs,
        int cursor,
        int limit)
    {
        var opened = await TraceArtifactReader.OpenAsync(
            tracePath, "localWeak", timeoutMs);
        if (!opened.IsSuccess)
        {
            return OperationResult.Failure(opened.Error!);
        }
        var artifact = opened.Artifact!;
        var settings = new Dictionary<string, string>
        {
            ["EVENT_OFFSET"] = cursor.ToString(CultureInfo.InvariantCulture),
            ["EVENT_LIMIT"] = limit.ToString(CultureInfo.InvariantCulture),
            ["EVENT_INCLUDE_ITEM_DATA"] = "0",
            ["MAX_DEPTH"] = "64",
            // The Event List reports column 1 as a single command index for one
            // command and as an inclusive span for a range. Filtering on the
            // separator keeps only span rows, so the bridge returns the range
            // skeleton instead of every draw, barrier and descriptor call.
            ["EVENT_FILTER_COLUMN"] = "1",
            ["EVENT_FILTER_CONTAINS"] = "-",
        };
        var probe = await ViewerProbeRunner.RunAsync(
            artifact,
            ViewerProbeMode.EventExport,
            "NsightSolidProbeEventListV1",
            settings,
            viewerPath,
            timeoutMs);
        if (!probe.IsSuccess)
        {
            return OperationResult.Failure(probe.Error!);
        }

        using var run = probe.Run!;
        try
        {
            var value = BridgeProjection.ProjectOutline(
                run.Document.RootElement, cursor, limit);
            return OperationResult.Success(
                value,
                [ViewerProbeRunner.CreateProvenance(run, artifact, "trace.outline/v1")],
                OperationSupport.ViewerWarnings);
        }
        catch (BridgeSchemaException exception)
        {
            return OperationSupport.ProjectionFailure(exception);
        }
    }
}
