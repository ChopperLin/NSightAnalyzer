using System.Globalization;
using NsightAnalyzer.Adapters.NsightViewer2026_2;
using NsightAnalyzer.Contracts;

namespace NsightAnalyzer.Operations;

/// <summary>
/// Retrieves the events whose description matches one name, together with the
/// ancestors of those matches, in a single decoder request.
/// <para>
/// This exists because scanning for a name by paging the Event List costs one
/// full model traversal per page: on a 306,576-event report that is 614
/// traversals to find a handful of rows. Pushing the name down makes it one.
/// </para>
/// <para>
/// This is not a product operation. It carries no judgement about which match
/// is wanted, and the occurrence/ambiguity policy stays in the wrapper.
/// </para>
/// </summary>
internal static class EventNameMatchOperation
{
    public static async Task<OperationResult> ExecuteAsync(
        string tracePath,
        string? viewerPath,
        int timeoutMs,
        string name,
        bool contains,
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
            ["EVENT_NAME_COLUMN"] = "0",
            // A filter removes exactly the rows that give a match its position
            // in the tree, so the ancestors are requested alongside it. Without
            // them the wrapper could report what it found but not where it is.
            ["EVENT_INCLUDE_ANCESTORS"] = "1",
        };
        if (contains)
        {
            settings["EVENT_NAME_CONTAINS"] = name;
        }
        else
        {
            settings["EVENT_NAME_EXACT"] = name;
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
            var value = BridgeProjection.ProjectEventNameMatches(
                run.Document.RootElement, cursor, limit, name, contains);
            return OperationResult.Success(
                value,
                [
                    ViewerProbeRunner.CreateProvenance(
                        run, artifact, "internal.event-name-matches/v1"),
                ],
                OperationSupport.ViewerWarnings);
        }
        catch (BridgeSchemaException exception)
        {
            return OperationSupport.ProjectionFailure(exception);
        }
    }
}
