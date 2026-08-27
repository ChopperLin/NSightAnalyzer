using System.Globalization;
using NsightAnalyzer.Adapters.NsightViewer2026_2;
using NsightAnalyzer.Contracts;

namespace NsightAnalyzer.Operations;

/// <summary>
/// Internal objective projection used by find-events. The decoder performs one
/// complete Event List traversal and pages only the matching subsequence while
/// retaining true EventKeys and shared ancestor context.
/// </summary>
internal static class EventCandidateOperation
{
    public static async Task<OperationResult> ExecuteAsync(
        string tracePath,
        string? viewerPath,
        int timeoutMs,
        string nameContains,
        int? withinEventOrdinal,
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
            ["EVENT_INCLUDE_ANCESTORS"] = "1",
            ["EVENT_NAME_COLUMN"] = "0",
            ["EVENT_NAME_CONTAINS"] = nameContains,
            ["MAX_DEPTH"] = "64",
        };
        if (withinEventOrdinal is not null)
        {
            settings["EVENT_WITHIN_ORDINAL"] =
                withinEventOrdinal.Value.ToString(CultureInfo.InvariantCulture);
        }

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
            var value = BridgeProjection.ProjectEventCandidates(
                run.Document.RootElement,
                cursor,
                limit,
                nameContains,
                withinEventOrdinal);
            return OperationResult.Success(
                value,
                [ViewerProbeRunner.CreateProvenance(
                    run, artifact, "internal.event-candidates/v1")],
                OperationSupport.ViewerWarnings);
        }
        catch (BridgeFactNotFoundException exception)
        {
            return OperationResult.Failure(
                ErrorCategory.NotFound,
                exception.Code,
                exception.Message);
        }
        catch (BridgeSchemaException exception)
        {
            return OperationSupport.ProjectionFailure(exception);
        }
    }
}
