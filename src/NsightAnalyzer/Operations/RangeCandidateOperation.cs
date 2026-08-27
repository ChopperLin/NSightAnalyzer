using System.Globalization;
using NsightAnalyzer.Adapters.NsightViewer2026_2;
using NsightAnalyzer.Contracts;

namespace NsightAnalyzer.Operations;

/// <summary>
/// Internal objective projection used by find-ranges. The bridge performs one
/// complete Event List traversal, orders the matched range subsequence, and
/// returns only the requested page plus its shared ancestors.
/// </summary>
internal static class RangeCandidateOperation
{
    public static async Task<OperationResult> ExecuteAsync(
        string tracePath,
        string? viewerPath,
        int timeoutMs,
        string? nameContains,
        string grain,
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
            ["EVENT_FILTER_COLUMN"] = "1",
            ["EVENT_FILTER_CONTAINS"] = "-",
            ["EVENT_SORT_DURATION_COLUMN"] = "10",
            ["MAX_DEPTH"] = "64",
        };
        if (nameContains is not null)
        {
            settings["EVENT_NAME_COLUMN"] = "0";
            settings["EVENT_NAME_CONTAINS"] = nameContains;
        }
        if (grain != EventGrain.All)
        {
            settings["EVENT_GRAIN_COLUMN"] = "0";
            settings["EVENT_GRAIN_PREFIXES"] =
                string.Join(';', EventGrain.ContainerPrefixes);
            settings["EVENT_GRAIN_SUBSTRINGS"] =
                string.Join(';', EventGrain.ContainerSubstrings);
            settings["EVENT_GRAIN_MODE"] =
                grain == EventGrain.Container ? "include" : "exclude";
        }
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
            var value = BridgeProjection.ProjectRangeCandidates(
                run.Document.RootElement,
                cursor,
                limit,
                nameContains,
                grain,
                withinEventOrdinal);
            return OperationResult.Success(
                value,
                [ViewerProbeRunner.CreateProvenance(
                    run, artifact, "internal.range-candidates/v1")],
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
