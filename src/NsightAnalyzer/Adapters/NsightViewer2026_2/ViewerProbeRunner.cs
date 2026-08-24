using System.Diagnostics;
using System.Text.Json;
using NsightAnalyzer.Contracts;

namespace NsightAnalyzer.Adapters.NsightViewer2026_2;

internal enum ViewerProbeMode
{
    Heartbeat,
    EventExport,
    SelectionMetricsExport,
}

internal sealed class ViewerProbeRun : IDisposable
{
    public ViewerProbeRun(
        JsonDocument document,
        string outputPath,
        TimeSpan duration,
        string mode)
    {
        Document = document;
        OutputPath = outputPath;
        Duration = duration;
        Mode = mode;
    }

    public JsonDocument Document { get; }
    public string OutputPath { get; }
    public TimeSpan Duration { get; }
    public string Mode { get; }

    public void Dispose() => Document.Dispose();
}

internal sealed record ViewerProbeRunResult(
    ViewerProbeRun? Run,
    OperationError? Error)
{
    public bool IsSuccess => Run is not null;
}

internal static class ViewerProbeRunner
{
    public const string AdapterName = "NsightViewer2026_2";
    public const string SupportLevel = "unsupportedVersionPinned";
    public const string ExpectedProductVersion = "2026.2.0.0";
    public const string ExpectedProductBuild = "37991608";
    public const string ExpectedProductSku = "public-release";
    public const string ExpectedQtVersion = "6.8.1";
    public const string ExpectedBridgeVersion = "probe-0.44";
    public const string DefaultViewerPath =
        @"C:\Program Files\NVIDIA Corporation\Nsight Graphics 2026.2.0\host\windows-desktop-nomad-x64\ngfx-ui.exe";

    private const long MaximumRawOutputBytes = 128L * 1024 * 1024;

    public static async Task<ViewerProbeRunResult> RunAsync(
        TraceArtifact artifact,
        ViewerProbeMode mode,
        string expectedSchema,
        IReadOnlyDictionary<string, string> settings,
        string? viewerPathOverride,
        int timeoutMs)
    {
        var viewerPath = string.IsNullOrWhiteSpace(viewerPathOverride)
            ? DefaultViewerPath
            : Path.GetFullPath(viewerPathOverride);
        if (!File.Exists(viewerPath))
        {
            return Fail(
                ErrorCategory.Unavailable,
                "viewer.not_found",
                "The pinned Nsight Graphics Viewer executable was not found.");
        }

        FileVersionInfo fileVersion;
        try
        {
            fileVersion = FileVersionInfo.GetVersionInfo(viewerPath);
        }
        catch (Exception exception)
        {
            return Fail(
                ErrorCategory.Unavailable,
                "viewer.version_unreadable",
                "The Viewer file version could not be read.",
                exception.GetType().Name);
        }
        if (!string.Equals(fileVersion.ProductVersion, "2026.2", StringComparison.Ordinal))
        {
            return Fail(
                ErrorCategory.AdapterMismatch,
                "viewer.version_mismatch",
                "The adapter only supports Nsight Graphics Viewer 2026.2 build 37991608.",
                $"observedProductVersion={fileVersion.ProductVersion ?? "unknown"}");
        }

        var viewerDirectory = Path.GetDirectoryName(viewerPath)!;
        var pluginPath = Path.Combine(viewerDirectory, "Plugins", "generic", "solidprobe.dll");
        if (!File.Exists(pluginPath))
        {
            return Fail(
                ErrorCategory.Unavailable,
                "viewer.bridge_not_installed",
                "The pinned SolidProbe Viewer plugin hook is not installed.");
        }

        string runDirectory;
        string outputPath;
        try
        {
            var configuredRoot = Environment.GetEnvironmentVariable("NSIGHT_ANALYZER_RUN_ROOT");
            var runRoot = Path.GetFullPath(string.IsNullOrWhiteSpace(configuredRoot)
                ? Path.Combine(Environment.CurrentDirectory, ".local", "runs")
                : configuredRoot);
            var shortId = Guid.NewGuid().ToString("N")[..12];
            runDirectory = Path.Combine(
                runRoot,
                $"{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{ModeName(mode)}-{shortId}");
            Directory.CreateDirectory(runDirectory);
            outputPath = Path.Combine(runDirectory, "bridge-output.json");
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException or ArgumentException)
        {
            return Fail(
                ErrorCategory.Unavailable,
                "viewer.run_directory_unavailable",
                "The local Viewer run directory could not be created.",
                exception.GetType().Name);
        }

        var requestId = Guid.NewGuid().ToString("N");
        var runStartedUtc = DateTime.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var startInfo = new ProcessStartInfo
        {
            FileName = viewerPath,
            WorkingDirectory = viewerDirectory,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(artifact.FullPath);
        startInfo.ArgumentList.Add("-plugin");
        startInfo.ArgumentList.Add("SolidProbe");
        RemoveHighConfidenceSecrets(startInfo);
        startInfo.Environment["NSIGHT_SOLID_PROBE_OUTPUT"] = outputPath;
        startInfo.Environment["NSIGHT_SOLID_PROBE_MODE"] = ModeName(mode);
        startInfo.Environment["NSIGHT_SOLID_PROBE_REQUEST_ID"] = requestId;
        startInfo.Environment["NSIGHT_SOLID_PROBE_REPORT_ID"] = artifact.ReportId;
        if (mode == ViewerProbeMode.Heartbeat)
        {
            startInfo.Environment["NSIGHT_SOLID_PROBE_QUIT_AFTER_HEARTBEAT"] = "1";
        }
        else
        {
            startInfo.Environment["NSIGHT_SOLID_PROBE_QUIT_WHEN_READY"] = "1";
        }
        foreach (var setting in settings)
        {
            if (!IsSafeSettingName(setting.Key))
            {
                return Fail(
                    ErrorCategory.Internal,
                    "viewer.setting_invalid",
                    "The adapter attempted to use an invalid bridge setting.");
            }
            var settingValue = setting.Value.Replace(
                "{RUN_DIRECTORY}", runDirectory, StringComparison.Ordinal);
            startInfo.Environment[$"NSIGHT_SOLID_PROBE_{setting.Key}"] = settingValue;
        }

        int? exitCode = null;
        try
        {
            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                return Fail(
                    ErrorCategory.Viewer,
                    "viewer.start_failed",
                    "Nsight Viewer did not start.");
            }

            var exitTask = process.WaitForExitAsync();
            if (await Task.WhenAny(exitTask, Task.Delay(timeoutMs)) != exitTask)
            {
                TryKillTree(process);
                await Task.WhenAny(exitTask, Task.Delay(5_000));
                stopwatch.Stop();
                await CloseNewCrashReportersAsync(viewerDirectory, runStartedUtc);
                return Fail(
                    ErrorCategory.Timeout,
                    "viewer.timeout",
                    "Nsight Viewer exceeded the operation timeout.",
                    $"timeoutMs={timeoutMs}; run={runDirectory}");
            }
            await exitTask;
            exitCode = process.ExitCode;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            stopwatch.Stop();
            return Fail(
                ErrorCategory.Viewer,
                "viewer.start_failed",
                "Nsight Viewer could not be started.",
                exception.GetType().Name);
        }

        await Task.Delay(500);
        var crashReporterCount = await CloseNewCrashReportersAsync(
            viewerDirectory, runStartedUtc);
        stopwatch.Stop();
        if (exitCode != 0)
        {
            return Fail(
                ErrorCategory.Viewer,
                "viewer.exit_nonzero",
                "Nsight Viewer exited with a nonzero code.",
                $"exitCode={exitCode}; run={runDirectory}");
        }
        if (crashReporterCount > 0)
        {
            return Fail(
                ErrorCategory.Viewer,
                "viewer.crash_reporter_spawned",
                "Nsight Viewer spawned CrashReporter during this operation.",
                $"count={crashReporterCount}; run={runDirectory}");
        }
        if (!File.Exists(outputPath))
        {
            return Fail(
                ErrorCategory.Viewer,
                "viewer.output_missing",
                "The expected bridge output was not written.",
                $"run={runDirectory}");
        }

        var output = new FileInfo(outputPath);
        if (output.LastWriteTimeUtc < runStartedUtc.AddSeconds(-1))
        {
            return Fail(
                ErrorCategory.Viewer,
                "viewer.output_stale",
                "The bridge output predates this request.",
                $"run={runDirectory}");
        }
        if (output.Length is <= 0 or > MaximumRawOutputBytes)
        {
            return Fail(
                ErrorCategory.Viewer,
                "viewer.output_size_invalid",
                "The bridge output size is outside the accepted bound.",
                $"bytes={output.Length}; maximumBytes={MaximumRawOutputBytes}");
        }

        JsonDocument document;
        try
        {
            await using var stream = new FileStream(
                outputPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            document = await JsonDocument.ParseAsync(
                stream,
                new JsonDocumentOptions { MaxDepth = 128 });
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return Fail(
                ErrorCategory.Viewer,
                "viewer.output_invalid",
                "The bridge output is not valid bounded JSON.",
                exception.GetType().Name);
        }

        var validationError = ValidateDocument(
            document.RootElement,
            artifact,
            requestId,
            expectedSchema);
        if (validationError is not null)
        {
            document.Dispose();
            return new(null, validationError);
        }

        return new(
            new ViewerProbeRun(document, outputPath, stopwatch.Elapsed, ModeName(mode)),
            null);
    }

    public static FactProvenance CreateProvenance(
        ViewerProbeRun run,
        TraceArtifact artifact,
        string projection,
        string source = "nsightViewerDecodedQtModels")
    {
        var root = run.Document.RootElement;
        return new(
            source,
            SupportLevel,
            ExpectedProductVersion,
            ExpectedProductBuild,
            ExpectedProductSku,
            RequiredString(root, "qtRuntimeVersion"),
            RequiredString(root, "pluginVersion"),
            artifact.ArtifactIdentity,
            RequiredString(root, "schema"),
            projection,
            run.Mode);
    }

    private static OperationError? ValidateDocument(
        JsonElement root,
        TraceArtifact artifact,
        string requestId,
        string expectedSchema)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return Error(
                ErrorCategory.Viewer,
                "viewer.output_shape_invalid",
                "The bridge output root is not an object.");
        }
        if (!TryString(root, "requestId", out var observedRequestId) ||
            observedRequestId != requestId)
        {
            return Error(
                ErrorCategory.Viewer,
                "viewer.request_identity_mismatch",
                "The bridge output request identity does not match this operation.");
        }
        if (!TryString(root, "reportId", out var reportId) ||
            reportId != artifact.ReportId)
        {
            return Error(
                ErrorCategory.Viewer,
                "viewer.trace_identity_mismatch",
                "The bridge output trace identity does not match this operation.");
        }
        if (!TryString(root, "schema", out var schema) || schema != expectedSchema)
        {
            return Error(
                ErrorCategory.AdapterMismatch,
                "viewer.schema_mismatch",
                "The bridge output schema does not match the pinned adapter.",
                $"expected={expectedSchema}; observed={schema ?? "missing"}");
        }
        if (!TryString(root, "applicationVersion", out var applicationVersion) ||
            applicationVersion is null ||
            !applicationVersion.Contains(ExpectedProductVersion, StringComparison.Ordinal) ||
            !applicationVersion.Contains(
                $"build {ExpectedProductBuild}", StringComparison.Ordinal) ||
            !applicationVersion.Contains(ExpectedProductSku, StringComparison.Ordinal))
        {
            return Error(
                ErrorCategory.AdapterMismatch,
                "viewer.build_mismatch",
                "The decoding Viewer version/build/SKU does not match the pinned adapter.",
                $"observed={applicationVersion ?? "missing"}");
        }
        if (!TryString(root, "qtRuntimeVersion", out var qtVersion) ||
            qtVersion != ExpectedQtVersion)
        {
            return Error(
                ErrorCategory.AdapterMismatch,
                "viewer.qt_mismatch",
                "The decoding Viewer Qt version does not match the pinned adapter.",
                $"observed={qtVersion ?? "missing"}");
        }
        if (!TryString(root, "pluginVersion", out var bridgeVersion) ||
            bridgeVersion != ExpectedBridgeVersion)
        {
            return Error(
                ErrorCategory.AdapterMismatch,
                "viewer.bridge_mismatch",
                "The loaded bridge version does not match the pinned adapter.",
                $"observed={bridgeVersion ?? "missing"}");
        }
        if (!root.TryGetProperty("verifiedHostTarget", out var verified) ||
            verified.ValueKind != JsonValueKind.Object ||
            !TryString(verified, "nsightVersion", out var verifiedVersion) ||
            verifiedVersion != "2026.2.0" ||
            !TryString(verified, "nsightBuild", out var verifiedBuild) ||
            verifiedBuild != ExpectedProductBuild)
        {
            return Error(
                ErrorCategory.AdapterMismatch,
                "viewer.host_unverified",
                "The bridge did not verify the expected Viewer host.");
        }
        if (!TryString(root, "status", out var status))
        {
            return Error(
                ErrorCategory.Viewer,
                "viewer.status_missing",
                "The bridge output has no status.");
        }
        if (status == "error")
        {
            var stage = TryString(root, "stage", out var value) ? value : "unknown";
            return stage == "event-target-not-found"
                ? Error(
                    ErrorCategory.NotFound,
                    "trace.event_not_found",
                    "The exact event selector was not found in the loaded trace.")
                : Error(
                    ErrorCategory.Viewer,
                    "viewer.bridge_error",
                    "The Viewer bridge returned a structured error.",
                    $"stage={stage}");
        }
        var acceptedStatus = expectedSchema == "NsightSolidProbeHeartbeatV1"
            ? status == "loaded"
            : status == "complete";
        if (!acceptedStatus)
        {
            return Error(
                ErrorCategory.Viewer,
                "viewer.output_incomplete",
                "The Viewer bridge output did not reach complete status.",
                $"status={status}");
        }
        return null;
    }

    private static string ModeName(ViewerProbeMode mode) => mode switch
    {
        ViewerProbeMode.Heartbeat => "heartbeat",
        ViewerProbeMode.EventExport => "event-export",
        ViewerProbeMode.SelectionMetricsExport => "selection-metrics-export",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    private static bool IsSafeSettingName(string name) =>
        !string.IsNullOrWhiteSpace(name) &&
        name.All(character =>
            character is >= 'A' and <= 'Z' ||
            character is >= '0' and <= '9' ||
            character == '_') &&
        name is not ("OUTPUT" or "MODE" or "REQUEST_ID" or "REPORT_ID" or
            "QUIT_WHEN_READY" or "QUIT_AFTER_HEARTBEAT");

    private static void TryKillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static async Task<int> CloseNewCrashReportersAsync(
        string viewerDirectory,
        DateTime runStartedUtc)
    {
        var expectedPath = Path.Combine(viewerDirectory, "CrashReporter.exe");
        var matches = new List<Process>();
        foreach (var process in Process.GetProcessesByName("CrashReporter"))
        {
            try
            {
                if (process.StartTime.ToUniversalTime() >= runStartedUtc.AddSeconds(-1) &&
                    string.Equals(
                        process.MainModule?.FileName,
                        expectedPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(process);
                }
                else
                {
                    process.Dispose();
                }
            }
            catch
            {
                process.Dispose();
            }
        }

        foreach (var process in matches)
        {
            using (process)
            {
                try
                {
                    process.CloseMainWindow();
                    if (!process.WaitForExit(500))
                    {
                        process.Kill();
                        await process.WaitForExitAsync();
                    }
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
        }
        return matches.Count;
    }

    private static void RemoveHighConfidenceSecrets(ProcessStartInfo startInfo)
    {
        string[] fragments =
        [
            "TOKEN", "SECRET", "PASSWORD", "PASSWD", "CREDENTIAL", "PRIVATE_KEY",
            "ACCESS_KEY", "API_KEY", "AUTH_SOCK",
        ];
        foreach (var name in startInfo.Environment.Keys.ToArray())
        {
            if (fragments.Any(fragment =>
                    name.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
            {
                startInfo.Environment.Remove(name);
            }
        }
    }

    private static bool TryString(
        JsonElement root,
        string property,
        out string? value)
    {
        if (root.TryGetProperty(property, out var element) &&
            element.ValueKind == JsonValueKind.String)
        {
            value = element.GetString();
            return true;
        }
        value = null;
        return false;
    }

    private static string RequiredString(JsonElement root, string property) =>
        root.GetProperty(property).GetString()!;

    private static ViewerProbeRunResult Fail(
        ErrorCategory category,
        string code,
        string message,
        string? detail = null) =>
        new(null, Error(category, code, message, detail));

    private static OperationError Error(
        ErrorCategory category,
        string code,
        string message,
        string? detail = null) =>
        new(category, code, message, detail);
}
