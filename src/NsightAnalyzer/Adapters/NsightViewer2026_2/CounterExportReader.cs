using System.Globalization;
using System.Text.Json;
using NsightAnalyzer.Contracts;
using NsightAnalyzer.Operations;

namespace NsightAnalyzer.Adapters.NsightViewer2026_2;

internal sealed record RawCounterColumn(
    int ColumnIndex,
    string Name,
    int NameOccurrence,
    string? Unit);

internal sealed record RawCounterRow(
    int RowIndex,
    string RangeName,
    int RangeOccurrence,
    IReadOnlyList<string> Cells);

internal sealed record CounterExportDataset(
    EventKey SeedScope,
    CounterExportInfo Export,
    IReadOnlyList<RawCounterColumn> Columns,
    IReadOnlyList<RawCounterRow> Rows);

internal sealed class CounterExportSession : IDisposable
{
    public CounterExportSession(
        ViewerProbeRun run,
        CounterExportDataset dataset,
        IReadOnlyList<OperationWarning> warnings)
    {
        Run = run;
        Dataset = dataset;
        Warnings = warnings;
    }

    public ViewerProbeRun Run { get; }
    public CounterExportDataset Dataset { get; }
    public IReadOnlyList<OperationWarning> Warnings { get; }

    public void Dispose() => Run.Dispose();
}

internal sealed record CounterExportSessionResult(
    CounterExportSession? Session,
    OperationError? Error)
{
    public bool IsSuccess => Session is not null;
}

internal static class CounterExportReader
{
    private const long MaximumRangeFileBytes = 512L * 1024 * 1024;
    private const int MaximumColumns = 10_000;
    private const int MaximumRows = 1_000_000;

    public static async Task<CounterExportSessionResult> ExportAsync(
        TraceArtifact artifact,
        string? viewerPath,
        int timeoutMs,
        int? eventOrdinal,
        string? eventPath)
    {
        var settings = TraceEventParametersOperation.ScopedSettings(
            eventOrdinal, eventPath);
        settings["ACTIVATE_PANEL"] = "FlatTabPanel_Timeline";
        settings["ACTIVATE_PANEL_VIA_BUTTON"] = "1";
        settings["PANEL_SETTLE_MIN_POLLS"] = "8";
        settings["INVOKE_CLASS_MATCH"] = "NV::UI::WarpScrubberView";
        settings["INVOKE_OBJECT_MATCH"] = "FlatTabPanel_Timeline";
        settings["INVOKE_MATCH_MODE"] = "exact";
        settings["INVOKE_OCCURRENCE"] = "0";
        settings["INVOKE_METHOD"] = "OnExportButtonClicked";
        settings["INVOKE_SETTLE_MIN_POLLS"] = "30";
        settings["DIALOG_AUTO_PATH"] = "{RUN_DIRECTORY}";
        settings["CLOSE_MODAL_BEFORE_QUIT"] = "1";

        var probe = await ViewerProbeRunner.RunAsync(
            artifact,
            ViewerProbeMode.SelectionMetricsExport,
            "NsightSolidProbeSelectionMetricsV1",
            settings,
            viewerPath,
            timeoutMs);
        if (!probe.IsSuccess)
        {
            return new(null, probe.Error);
        }

        var run = probe.Run!;
        try
        {
            var runDirectory = Path.GetDirectoryName(run.OutputPath)!;
            var seedScope = ValidateBridge(
                run.Document.RootElement,
                runDirectory,
                eventOrdinal,
                eventPath);
            var rangeFile = Path.Combine(
                runDirectory, "BASE_UNLOCKED", "GPUTRACE_REGIMES.xls");
            var parsed = ReadRangeFile(rangeFile);
            var warnings = new List<OperationWarning>();
            var cleanup = CleanupTraceCopy(
                runDirectory, artifact, warnings);
            var evidenceFiles = Directory
                .EnumerateFiles(runDirectory, "*", SearchOption.AllDirectories)
                .Where(path => !path.Equals(
                    run.OutputPath, StringComparison.OrdinalIgnoreCase))
                .Where(path => !path.Equals(
                    Path.Combine(runDirectory, artifact.FileName),
                    StringComparison.OrdinalIgnoreCase))
                .Select(path => Path.GetRelativePath(runDirectory, path))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
            var export = new CounterExportInfo(
                runDirectory,
                "viewerTsvWithXlsExtension",
                rangeFile,
                parsed.Rows.Count,
                parsed.Columns.Count,
                cleanup,
                evidenceFiles);
            return new(
                new(
                    run,
                    new(seedScope, export, parsed.Columns, parsed.Rows),
                    warnings),
                null);
        }
        catch (BridgeSchemaException exception)
        {
            run.Dispose();
            return Fail(
                ErrorCategory.AdapterMismatch,
                "counter.export_schema_mismatch",
                "The Viewer counter export shape changed.",
                exception.Message);
        }
        catch (FileNotFoundException exception)
        {
            run.Dispose();
            return Fail(
                ErrorCategory.Viewer,
                "counter.export_missing",
                "The Viewer did not create the expected range counter file.",
                exception.FileName);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            run.Dispose();
            return Fail(
                ErrorCategory.Viewer,
                "counter.export_invalid",
                "The Viewer range counter export could not be validated.",
                exception.Message);
        }
    }

    private static EventKey ValidateBridge(
        JsonElement root,
        string runDirectory,
        int? requestedOrdinal,
        string? requestedPath)
    {
        if (!RequiredBoolean(root, "selectionMatchesTarget") ||
            RequiredString(root, "requestedPanel") != "FlatTabPanel_Timeline" ||
            RequiredString(root, "invokeMethod") != "OnExportButtonClicked" ||
            !RequiredBoolean(root, "invokeCompleted") ||
            !RequiredBoolean(root, "invokeSucceeded") ||
            !RequiredBoolean(root, "dialogSeen") ||
            !RequiredBoolean(root, "dialogAcceptQueued"))
        {
            throw new BridgeSchemaException(
                "The counter export action or directory dialog did not complete.");
        }
        var autoPath = Path.GetFullPath(RequiredString(root, "dialogAutoPath"));
        if (!autoPath.Equals(
                Path.GetFullPath(runDirectory), StringComparison.OrdinalIgnoreCase))
        {
            throw new BridgeSchemaException(
                "The counter export directory differs from the isolated run directory.");
        }
        var invokeTarget = RequiredObject(root, "invokeTarget");
        if (RequiredString(invokeTarget, "class") != "NV::UI::WarpScrubberView" ||
            RequiredString(invokeTarget, "objectName") != "FlatTabPanel_Timeline")
        {
            throw new BridgeSchemaException(
                "The counter export target identity changed.");
        }

        var selector = RequiredObject(root, "eventSelector");
        if (requestedOrdinal is not null)
        {
            if (RequiredString(selector, "kind") != "ordinal" ||
                RequiredInt32(selector, "value") != requestedOrdinal.Value)
            {
                throw new BridgeSchemaException(
                    "The counter export event ordinal differs from the request.");
            }
        }
        else if (RequiredString(selector, "kind") != "path" ||
            RequiredString(selector, "value") != requestedPath!.Replace('.', '/'))
        {
            throw new BridgeSchemaException(
                "The counter export event path differs from the request.");
        }

        var target = RequiredObject(root, "targetSelection");
        var current = RequiredObject(root, "currentSelection");
        var targetPath = RequiredInt32Array(target, "path");
        var currentPath = RequiredInt32Array(current, "path");
        if (!targetPath.SequenceEqual(currentPath) ||
            requestedPath is not null && !targetPath.SequenceEqual(
                requestedPath.Split('.').Select(int.Parse)))
        {
            throw new BridgeSchemaException(
                "The counter export selection path differs from the request.");
        }
        var cells = RequiredArray(target, "cells");
        return new(
            requestedOrdinal,
            targetPath,
            ArrayText(cells, 1),
            ArrayText(cells, 0) ?? string.Empty);
    }

    private static (IReadOnlyList<RawCounterColumn> Columns,
        IReadOnlyList<RawCounterRow> Rows) ReadRangeFile(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists)
        {
            throw new FileNotFoundException(
                "GPUTRACE_REGIMES.xls was not created.", path);
        }
        if (file.Length is <= 0 or > MaximumRangeFileBytes)
        {
            throw new InvalidDataException(
                $"Counter range file size {file.Length} is outside the accepted bound.");
        }

        using var reader = new StreamReader(path, detectEncodingFromByteOrderMarks: true);
        var headerLine = reader.ReadLine();
        if (headerLine is null)
        {
            throw new InvalidDataException("Counter range file has no header.");
        }
        var headers = headerLine.Split('\t');
        if (headers.Length is < 2 or > MaximumColumns ||
            headers[0] != "flattened_event_name")
        {
            throw new InvalidDataException(
                "Counter range header is missing flattened_event_name or is out of bounds.");
        }

        var headerOccurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        var columns = new List<RawCounterColumn>(headers.Length - 1);
        for (var index = 1; index < headers.Length; index++)
        {
            var name = headers[index];
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidDataException(
                    $"Counter column {index} has an empty name.");
            }
            var occurrence = headerOccurrences.GetValueOrDefault(name);
            headerOccurrences[name] = occurrence + 1;
            columns.Add(new(index, name, occurrence, Unit(name)));
        }

        var rangeOccurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        var rows = new List<RawCounterRow>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (rows.Count >= MaximumRows)
            {
                throw new InvalidDataException(
                    "Counter range row count exceeds the accepted bound.");
            }
            var cells = line.Split('\t');
            if (cells.Length != headers.Length)
            {
                throw new InvalidDataException(
                    $"Counter row {rows.Count} has {cells.Length} cells; expected {headers.Length}.");
            }
            var rangeName = cells[0];
            var occurrence = rangeOccurrences.GetValueOrDefault(rangeName);
            rangeOccurrences[rangeName] = occurrence + 1;
            rows.Add(new(rows.Count, rangeName, occurrence, cells));
        }
        return (columns, rows);
    }

    private static string CleanupTraceCopy(
        string runDirectory,
        TraceArtifact artifact,
        ICollection<OperationWarning> warnings)
    {
        var copyPath = Path.GetFullPath(Path.Combine(
            runDirectory, artifact.FileName));
        if (!Path.GetDirectoryName(copyPath)!.Equals(
                Path.GetFullPath(runDirectory), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The generated trace-copy path escaped the isolated run directory.");
        }
        var copy = new FileInfo(copyPath);
        if (!copy.Exists)
        {
            return "notPresent";
        }
        if (copy.Length != artifact.ByteLength)
        {
            warnings.Add(new(
                "counter.trace_copy_retained",
                "The Viewer-generated trace copy was retained because its length differs from the input."));
            return "retainedIdentityMismatch";
        }
        try
        {
            copy.Delete();
            return "removed";
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            warnings.Add(new(
                "counter.trace_copy_cleanup_failed",
                "The validated Viewer-generated trace copy could not be removed."));
            return "retainedCleanupFailed";
        }
    }

    private static string? Unit(string name)
    {
        if (name.EndsWith(".pct", StringComparison.OrdinalIgnoreCase) ||
            name.Contains(".pct_of_peak_", StringComparison.OrdinalIgnoreCase))
        {
            return "%";
        }
        if (name.EndsWith(".ratio", StringComparison.OrdinalIgnoreCase))
        {
            return "ratio";
        }
        if (name.EndsWith(".sum.per_second", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".avg.per_second", StringComparison.OrdinalIgnoreCase))
        {
            return "perSecond";
        }
        return null;
    }

    private static CounterExportSessionResult Fail(
        ErrorCategory category,
        string code,
        string message,
        string? detail = null) =>
        new(null, new(category, code, message, detail));

    private static JsonElement RequiredObject(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.Object)
        {
            throw new BridgeSchemaException($"Required object '{name}' is missing.");
        }
        return value;
    }

    private static JsonElement RequiredArray(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            throw new BridgeSchemaException($"Required array '{name}' is missing.");
        }
        return value;
    }

    private static string RequiredString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            throw new BridgeSchemaException($"Required string '{name}' is missing.");
        }
        return value.GetString()!;
    }

    private static int RequiredInt32(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out var number))
        {
            throw new BridgeSchemaException($"Required integer '{name}' is missing.");
        }
        return number;
    }

    private static bool RequiredBoolean(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) ||
            value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new BridgeSchemaException($"Required boolean '{name}' is missing.");
        }
        return value.GetBoolean();
    }

    private static IReadOnlyList<int> RequiredInt32Array(
        JsonElement parent,
        string name)
    {
        var result = new List<int>();
        foreach (var value in RequiredArray(parent, name).EnumerateArray())
        {
            if (!value.TryGetInt32(out var number) || number < 0)
            {
                throw new BridgeSchemaException(
                    $"Array '{name}' contains an invalid row index.");
            }
            result.Add(number);
        }
        return result;
    }

    private static string? ArrayText(JsonElement array, int index) =>
        index >= 0 && index < array.GetArrayLength()
            ? array[index].ValueKind == JsonValueKind.String
                ? array[index].GetString()
                : array[index].ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                    ? null
                    : array[index].ToString()
            : null;
}
