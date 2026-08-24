using System.Globalization;
using NsightAnalyzer.Adapters.NsightViewer2026_2;
using NsightAnalyzer.Contracts;

namespace NsightAnalyzer.Operations;

internal static class TraceEventsOperation
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
            var value = BridgeProjection.ProjectEvents(
                run.Document.RootElement, cursor, limit);
            return OperationResult.Success(
                value,
                [ViewerProbeRunner.CreateProvenance(run, artifact, "trace.events/v1")],
                OperationSupport.ViewerWarnings);
        }
        catch (BridgeSchemaException exception)
        {
            return OperationSupport.ProjectionFailure(exception);
        }
    }
}
