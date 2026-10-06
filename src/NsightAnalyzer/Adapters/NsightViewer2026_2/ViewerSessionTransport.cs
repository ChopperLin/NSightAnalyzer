using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NsightAnalyzer.Contracts;

namespace NsightAnalyzer.Adapters.NsightViewer2026_2;

internal sealed record ViewerSessionInvocationResult(OperationError? Error)
{
    public bool IsSuccess => Error is null;
}

internal sealed record ViewerSessionCloseResult(
    ViewerSessionCloseValue? Value,
    OperationError? Error)
{
    public bool IsSuccess => Value is not null;
}

internal sealed record ViewerSessionCloseValue(bool HadSession, string Shutdown);

internal sealed record ViewerSessionIdentity(
    string Directory, string SessionId, int Pid, long ProcessStartUtcTicks);

internal static class ViewerSessionTransport
{
    internal static ViewerSessionIdentity? TryGetLiveIdentity(
        TraceArtifact artifact, string viewerPath, string? completedRequestId = null)
    {
        try
        {
            var candidates = ViewerHostTargets.FromExecutable(viewerPath);
            if (candidates.Count == 0)
            {
                return null;
            }
            var directory = Path.Combine(ResolveSessionRoot(), $"trace-{SessionKey(artifact, viewerPath)[..32]}");
            var owner = TryReadOwner(Path.Combine(directory, "owner.json"));
            var manifest = TryReadManifest(Path.Combine(directory, "session.json"));
            if (owner is null || manifest is null || manifest.Status != "ready" ||
                completedRequestId is not null &&
                    (completedRequestId.Length == 0 || manifest.LastRequestId != completedRequestId) ||
                !ValidateManifest(manifest, owner.SessionId, artifact.ReportId, owner.Pid, candidates) ||
                !TryOpenExpectedProcess(owner, artifact, viewerPath, out var process))
            {
                return null;
            }
            using (process)
            {
                return new(directory, owner.SessionId, owner.Pid, owner.ProcessStartUtcTicks);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private const string OwnerSchema = "NsightAnalyzerViewerSessionOwnerV1";
    private const string ManifestSchema = "NsightSolidProbeSessionV1";
    private const string RequestSchema = "NsightSolidProbeSessionRequestV1";
    private const string CloseSchema = "NsightSolidProbeSessionCloseV1";
    private const int DefaultIdleTimeoutMs = 300_000;
    private const int MinimumIdleTimeoutMs = 10_000;
    private const int MaximumIdleTimeoutMs = 3_600_000;
    private const int MaximumControlFileBytes = 1024 * 1024;
    private const int StartupHandshakeTimeoutMs = 30_000;

    public static async Task<ViewerSessionInvocationResult> ExecuteAsync(
        TraceArtifact artifact,
        string viewerPath,
        string viewerDirectory,
        string mode,
        string expectedSchema,
        IReadOnlyDictionary<string, string> settings,
        string runRoot,
        string sessionRoot,
        string runDirectory,
        string outputPath,
        string requestId,
        int timeoutMs,
        string cachePolicy,
        IReadOnlyList<ViewerHostTarget> candidates)
    {
        var stopwatch = Stopwatch.StartNew();
        string sessionDirectory;
        try
        {
            Directory.CreateDirectory(sessionRoot);
            sessionDirectory = Path.Combine(
                sessionRoot, $"trace-{SessionKey(artifact, viewerPath)[..32]}");
            Directory.CreateDirectory(sessionDirectory);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException or ArgumentException)
        {
            return Fail(
                ErrorCategory.Unavailable,
                "viewer.session_directory_unavailable",
                "The local Viewer session directory could not be created.",
                exception.GetType().Name);
        }

        var lockResult = await AcquireLockAsync(
            Path.Combine(sessionDirectory, "transport.lock"),
            stopwatch,
            timeoutMs);
        if (lockResult.Stream is null)
        {
            return new(lockResult.Error);
        }
        await using var sessionLock = lockResult.Stream;

        var ensured = await EnsureSessionAsync(
            artifact,
            viewerPath,
            viewerDirectory,
            runRoot,
            sessionDirectory,
            stopwatch,
            timeoutMs,
            candidates);
        if (!ensured.IsSuccess)
        {
            return new(ensured.Error);
        }

        using var sessionProcess = ensured.Process!;
        var requestPath = Path.Combine(sessionDirectory, "request.json");
        try
        {
            if (File.Exists(requestPath))
            {
                await InvalidateAsync(sessionProcess);
                return Fail(
                    ErrorCategory.Viewer,
                    "viewer.session_protocol_error",
                    "The Viewer session mailbox contains a stale request.",
                    $"session={sessionDirectory}");
            }

            await WriteJsonAtomicallyAsync(
                requestPath,
                new
                {
                    schema = RequestSchema,
                    sessionId = ensured.SessionId,
                    requestId,
                    reportId = artifact.ReportId,
                    mode,
                    expectedSchema,
                    cachePolicy,
                    outputPath,
                    settings,
                });
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException or JsonException)
        {
            await InvalidateAsync(sessionProcess);
            return Fail(
                ErrorCategory.Unavailable,
                "viewer.session_request_unavailable",
                "The Viewer session request could not be written.",
                exception.GetType().Name);
        }

        var completed = await WaitForRequestAsync(
            sessionProcess,
            sessionDirectory,
            ensured.SessionId!,
            artifact.ReportId,
            requestId,
            stopwatch,
            timeoutMs,
            candidates);
        if (!completed.IsSuccess)
        {
            await InvalidateAsync(sessionProcess);
            await CloseUnexpectedCrashReportersAsync(
                viewerDirectory,
                sessionProcess.Id,
                ensured.BaselineCrashReporterPids ?? []);
            return new(completed.Error);
        }

        await Task.Delay(500);
        if (!IsExpectedProcess(sessionProcess, viewerPath, ensured.ProcessStartUtcTicks))
        {
            await CloseUnexpectedCrashReportersAsync(
                viewerDirectory,
                sessionProcess.Id,
                ensured.BaselineCrashReporterPids ?? []);
            return Fail(
                ErrorCategory.Viewer,
                "viewer.session_lost",
                "The Viewer session ended after producing its response.",
                $"session={sessionDirectory}");
        }

        var crashReporterCount = await CloseUnexpectedCrashReportersAsync(
            viewerDirectory,
            sessionProcess.Id,
            ensured.BaselineCrashReporterPids ?? []);
        if (crashReporterCount > 0)
        {
            await InvalidateAsync(sessionProcess);
            return Fail(
                ErrorCategory.Viewer,
                "viewer.crash_reporter_spawned",
                "Nsight Viewer spawned an unexpected CrashReporter during this operation.",
                $"count={crashReporterCount}; run={runDirectory}");
        }
        return new(null);
    }

    public static async Task<ViewerSessionCloseResult> CloseAsync(
        TraceArtifact artifact,
        string viewerPath,
        int timeoutMs)
    {
        var stopwatch = Stopwatch.StartNew();
        string sessionDirectory;
        try
        {
            var sessionRoot = ResolveSessionRoot();
            sessionDirectory = Path.Combine(
                sessionRoot, $"trace-{SessionKey(artifact, viewerPath)[..32]}");
            if (!Directory.Exists(sessionDirectory))
            {
                return new(new(false, "notFound"), null);
            }
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException or ArgumentException)
        {
            return CloseFail(
                ErrorCategory.Unavailable,
                "viewer.session_directory_unavailable",
                "The local Viewer session directory could not be inspected.",
                exception.GetType().Name);
        }

        var lockResult = await AcquireLockAsync(
            Path.Combine(sessionDirectory, "transport.lock"),
            stopwatch,
            timeoutMs);
        if (lockResult.Stream is null)
        {
            return new(null, lockResult.Error);
        }
        await using var sessionLock = lockResult.Stream;

        var owner = TryReadOwner(Path.Combine(sessionDirectory, "owner.json"));
        if (owner is null ||
            !TryOpenExpectedProcess(owner, artifact, viewerPath, out var process))
        {
            return new(new(false, "notFound"), null);
        }
        using (process)
        {
            var graceful = false;
            try
            {
                await WriteJsonAtomicallyAsync(
                    Path.Combine(sessionDirectory, "close.json"),
                    new { schema = CloseSchema, sessionId = owner.SessionId });
                while (RemainingMilliseconds(stopwatch, timeoutMs) > 0)
                {
                    if (process.HasExited)
                    {
                        graceful = true;
                        break;
                    }
                    await Task.Delay(Math.Min(100, RemainingMilliseconds(stopwatch, timeoutMs)));
                }
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or
                    System.ComponentModel.Win32Exception or IOException or
                    UnauthorizedAccessException)
            {
                await InvalidateAsync(process);
                return CloseFail(
                    ErrorCategory.Viewer,
                    "viewer.session_close_failed",
                    "The Viewer session could not be closed cleanly.",
                    exception.GetType().Name);
            }

            if (!process.HasExited)
            {
                await InvalidateAsync(process);
            }
            await CloseBaselineCrashReportersAsync(
                Path.GetDirectoryName(viewerPath)!,
                owner.Pid,
                owner.BaselineCrashReporterPids);
            DeleteKnownControlFile(Path.Combine(sessionDirectory, "close.json"));
            DeleteKnownControlFile(Path.Combine(sessionDirectory, "request.json"));
            return new(new(true, graceful ? "graceful" : "forced"), null);
        }
    }

    public static string ResolveRunRoot()
    {
        var configured = Environment.GetEnvironmentVariable("NSIGHT_ANALYZER_RUN_ROOT");
        return Path.GetFullPath(string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.CurrentDirectory, ".local", "runs")
            : configured);
    }

    public static string ResolveSessionRoot()
    {
        var configured = Environment.GetEnvironmentVariable("NSIGHT_ANALYZER_SESSION_ROOT");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }
        var configuredRunRoot = Environment.GetEnvironmentVariable(
            "NSIGHT_ANALYZER_RUN_ROOT");
        return string.IsNullOrWhiteSpace(configuredRunRoot)
            ? Path.GetFullPath(Path.Combine(
                Environment.CurrentDirectory, ".local", "sessions"))
            : Path.Combine(Path.GetFullPath(configuredRunRoot), ".sessions");
    }

    private static async Task<EnsureSessionResult> EnsureSessionAsync(
        TraceArtifact artifact,
        string viewerPath,
        string viewerDirectory,
        string runRoot,
        string sessionDirectory,
        Stopwatch stopwatch,
        int timeoutMs,
        IReadOnlyList<ViewerHostTarget> candidates)
    {
        var ownerPath = Path.Combine(sessionDirectory, "owner.json");
        var manifestPath = Path.Combine(sessionDirectory, "session.json");
        var owner = TryReadOwner(ownerPath);
        if (owner is not null &&
            TryOpenExpectedProcess(owner, artifact, viewerPath, out var existingProcess))
        {
            var reusable = await WaitForReusableManifestAsync(
                existingProcess,
                owner,
                manifestPath,
                stopwatch,
                timeoutMs,
                candidates);
            if (reusable)
            {
                return new(
                    existingProcess,
                    owner.SessionId,
                    owner.ProcessStartUtcTicks,
                    owner.BaselineCrashReporterPids,
                    null);
            }
            await InvalidateAsync(existingProcess);
            existingProcess.Dispose();
        }
        if (owner is not null)
        {
            await CloseBaselineCrashReportersAsync(
                viewerDirectory,
                owner.Pid,
                owner.BaselineCrashReporterPids);
        }

        DeleteKnownControlFile(ownerPath);
        DeleteKnownControlFile(manifestPath);
        DeleteKnownControlFile(Path.Combine(sessionDirectory, "request.json"));
        DeleteKnownControlFile(Path.Combine(sessionDirectory, "close.json"));

        var sessionId = Guid.NewGuid().ToString("N");
        var startInfo = new ProcessStartInfo
        {
            FileName = viewerPath,
            WorkingDirectory = viewerDirectory,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Normal,
            ErrorDialog = false,
        };
        startInfo.ArgumentList.Add(artifact.FullPath);
        startInfo.ArgumentList.Add("-plugin");
        startInfo.ArgumentList.Add("SolidProbe");
        ViewerProbeRunner.RemoveHighConfidenceSecrets(startInfo);
        startInfo.Environment["NSIGHT_SOLID_PROBE_SESSION_DIRECTORY"] = sessionDirectory;
        startInfo.Environment["NSIGHT_SOLID_PROBE_SESSION_RUN_ROOT"] = runRoot;
        startInfo.Environment["NSIGHT_SOLID_PROBE_SESSION_ID"] = sessionId;
        startInfo.Environment["NSIGHT_SOLID_PROBE_REPORT_ID"] = artifact.ReportId;
        startInfo.Environment["NSIGHT_SOLID_PROBE_VERIFIED_HOST_TARGETS"] =
            ViewerHostTargets.BridgeVerificationJson(candidates);
        startInfo.Environment["NSIGHT_SOLID_PROBE_SESSION_IDLE_TIMEOUT_MS"] =
            SessionIdleTimeoutMs().ToString(System.Globalization.CultureInfo.InvariantCulture);

        Process process;
        try
        {
            process = ViewerDetachedProcessLauncher.Start(startInfo);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
                System.ComponentModel.Win32Exception or ArgumentException)
        {
            return EnsureFail(
                ErrorCategory.Viewer,
                "viewer.start_failed",
                "Nsight Viewer could not be started.",
                exception.GetType().Name);
        }

        long processStartUtcTicks;
        try
        {
            processStartUtcTicks = process.StartTime.ToUniversalTime().Ticks;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
                System.ComponentModel.Win32Exception)
        {
            await InvalidateAsync(process);
            process.Dispose();
            return EnsureFail(
                ErrorCategory.Viewer,
                "viewer.start_failed",
                "Nsight Viewer process identity could not be established.",
                exception.GetType().Name);
        }
        try
        {
            await WriteJsonAtomicallyAsync(
                ownerPath,
                new
                {
                    schema = OwnerSchema,
                    sessionKey = SessionKey(artifact, viewerPath),
                    sessionId,
                    reportId = artifact.ReportId,
                    viewerPath = Path.GetFullPath(viewerPath),
                    pid = process.Id,
                    processStartUtcTicks,
                    baselineCrashReporterPids = Array.Empty<int>(),
                });
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException or JsonException)
        {
            await InvalidateAsync(process);
            process.Dispose();
            return EnsureFail(
                ErrorCategory.Unavailable,
                "viewer.session_owner_unavailable",
                "The Viewer session owner record could not be written.",
                exception.GetType().Name);
        }

        var handshakeLimit = Math.Min(
            StartupHandshakeTimeoutMs,
            RemainingMilliseconds(stopwatch, timeoutMs));
        var handshake = Stopwatch.StartNew();
        while (handshake.ElapsedMilliseconds < handshakeLimit &&
            RemainingMilliseconds(stopwatch, timeoutMs) > 0)
        {
            if (process.HasExited)
            {
                var exitCode = process.ExitCode;
                var crashCount = await CloseUnexpectedCrashReportersAsync(
                    viewerDirectory, process.Id, []);
                process.Dispose();
                return crashCount > 0
                    ? EnsureFail(
                        ErrorCategory.Viewer,
                        "viewer.crash_reporter_spawned",
                        "Nsight Viewer spawned an unexpected CrashReporter while starting a session.",
                        $"count={crashCount}")
                    : EnsureFail(
                        ErrorCategory.Viewer,
                        "viewer.exit_nonzero",
                        "Nsight Viewer exited while starting a session.",
                        $"exitCode={exitCode}");
            }
            var manifest = TryReadManifest(manifestPath);
            if (manifest is not null &&
                ValidateManifest(manifest, sessionId, artifact.ReportId, process.Id, candidates) &&
                manifest.Status == "ready")
            {
                var baselineDelay = Math.Min(
                    2_500,
                    RemainingMilliseconds(stopwatch, timeoutMs));
                if (baselineDelay > 0)
                {
                    await Task.Delay(baselineDelay);
                }
                if (!IsExpectedProcess(process, viewerPath, processStartUtcTicks))
                {
                    await CloseUnexpectedCrashReportersAsync(
                        viewerDirectory, process.Id, []);
                    process.Dispose();
                    return EnsureFail(
                        ErrorCategory.Viewer,
                        "viewer.session_lost",
                        "The Viewer session ended during startup stabilization.");
                }
                var baselineCrashReporterPids = SnapshotCrashReporterIds(
                        viewerDirectory, process.Id)
                    .OrderBy(pid => pid)
                    .ToArray();
                try
                {
                    await WriteJsonAtomicallyAsync(
                        ownerPath,
                        new
                        {
                            schema = OwnerSchema,
                            sessionKey = SessionKey(artifact, viewerPath),
                            sessionId,
                            reportId = artifact.ReportId,
                            viewerPath = Path.GetFullPath(viewerPath),
                            pid = process.Id,
                            processStartUtcTicks,
                            baselineCrashReporterPids,
                        });
                }
                catch (Exception exception) when (
                    exception is UnauthorizedAccessException or IOException or JsonException)
                {
                    await InvalidateAsync(process);
                    process.Dispose();
                    return EnsureFail(
                        ErrorCategory.Unavailable,
                        "viewer.session_owner_unavailable",
                        "The Viewer session owner record could not be updated.",
                        exception.GetType().Name);
                }
                return new(
                    process,
                    sessionId,
                    processStartUtcTicks,
                    baselineCrashReporterPids,
                    null);
            }
            await Task.Delay(100);
        }

        await InvalidateAsync(process);
        await CloseUnexpectedCrashReportersAsync(viewerDirectory, process.Id, []);
        process.Dispose();
        return EnsureFail(
            ErrorCategory.AdapterMismatch,
            "viewer.session_handshake_failed",
            "The installed Viewer bridge did not establish the pinned session protocol.",
            $"expectedBridge={ViewerProbeRunner.ExpectedBridgeVersion}; session={sessionDirectory}");
    }

    private static async Task<bool> WaitForReusableManifestAsync(
        Process process,
        SessionOwner owner,
        string manifestPath,
        Stopwatch stopwatch,
        int timeoutMs,
        IReadOnlyList<ViewerHostTarget> candidates)
    {
        while (RemainingMilliseconds(stopwatch, timeoutMs) > 0)
        {
            if (!IsExpectedProcess(
                    process, owner.ViewerPath, owner.ProcessStartUtcTicks))
            {
                return false;
            }
            var manifest = TryReadManifest(manifestPath);
            if (manifest is null ||
                !ValidateManifest(
                    manifest, owner.SessionId, owner.ReportId, owner.Pid, candidates))
            {
                await Task.Delay(100);
                continue;
            }
            if (manifest.Status == "ready")
            {
                return true;
            }
            if (manifest.Status is "poisoned" or "closing")
            {
                return false;
            }
            await Task.Delay(100);
        }
        return false;
    }

    private static async Task<ViewerSessionInvocationResult> WaitForRequestAsync(
        Process process,
        string sessionDirectory,
        string sessionId,
        string reportId,
        string requestId,
        Stopwatch stopwatch,
        int timeoutMs,
        IReadOnlyList<ViewerHostTarget> candidates)
    {
        var manifestPath = Path.Combine(sessionDirectory, "session.json");
        while (RemainingMilliseconds(stopwatch, timeoutMs) > 0)
        {
            if (process.HasExited)
            {
                return Fail(
                    ErrorCategory.Viewer,
                    "viewer.session_lost",
                    "The Viewer session ended before the atom completed.",
                    $"session={sessionDirectory}");
            }
            var manifest = TryReadManifest(manifestPath);
            if (manifest is not null &&
                ValidateManifest(manifest, sessionId, reportId, process.Id, candidates))
            {
                if (manifest.Status == "ready" &&
                    manifest.LastRequestId == requestId)
                {
                    return new(null);
                }
                if (manifest.Status == "poisoned")
                {
                    return Fail(
                        ErrorCategory.Viewer,
                        "viewer.session_poisoned",
                        "The atom left the Viewer session unsafe for reuse.",
                        $"reason={manifest.ProtocolError ?? "unknown"}; session={sessionDirectory}");
                }
                if (manifest.Status == "closing")
                {
                    return Fail(
                        ErrorCategory.Viewer,
                        "viewer.session_lost",
                        "The Viewer session closed before the atom completed.",
                        $"session={sessionDirectory}");
                }
            }
            await Task.Delay(Math.Min(100, RemainingMilliseconds(stopwatch, timeoutMs)));
        }
        return Fail(
            ErrorCategory.Timeout,
            "viewer.timeout",
            "Nsight Viewer exceeded the operation timeout.",
            $"timeoutMs={timeoutMs}; session={sessionDirectory}");
    }

    private static async Task<LockResult> AcquireLockAsync(
        string path,
        Stopwatch stopwatch,
        int timeoutMs)
    {
        while (RemainingMilliseconds(stopwatch, timeoutMs) > 0)
        {
            try
            {
                return new(
                    new FileStream(
                        path,
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None,
                        1,
                        FileOptions.Asynchronous),
                    null);
            }
            catch (IOException)
            {
                await Task.Delay(Math.Min(100, RemainingMilliseconds(stopwatch, timeoutMs)));
            }
            catch (Exception exception) when (
                exception is UnauthorizedAccessException or ArgumentException)
            {
                return new(
                    null,
                    new(
                        ErrorCategory.Unavailable,
                        "viewer.session_lock_unavailable",
                        "The Viewer session lock could not be opened.",
                        exception.GetType().Name));
            }
        }
        return new(
            null,
            new(
                ErrorCategory.Timeout,
                "viewer.session_queue_timeout",
                "Waiting for the serialized Viewer session exceeded the operation timeout.",
                $"timeoutMs={timeoutMs}"));
    }

    private static SessionOwner? TryReadOwner(string path)
    {
        try
        {
            using var document = ReadBoundedJson(path, 64 * 1024);
            if (document is null)
            {
                return null;
            }
            var root = document.RootElement;
            if (String(root, "schema") != OwnerSchema ||
                !TryInt32(root, "pid", out var pid) ||
                !TryInt64(root, "processStartUtcTicks", out var startTicks))
            {
                return null;
            }
            return new(
                String(root, "sessionKey") ?? string.Empty,
                String(root, "sessionId") ?? string.Empty,
                String(root, "reportId") ?? string.Empty,
                String(root, "viewerPath") ?? string.Empty,
                pid,
                startTicks,
                Int32Array(root, "baselineCrashReporterPids"));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static SessionManifest? TryReadManifest(string path)
    {
        try
        {
            using var document = ReadBoundedJson(path, 64 * 1024);
            if (document is null)
            {
                return null;
            }
            var root = document.RootElement;
            if (String(root, "schema") != ManifestSchema ||
                !TryInt32(root, "pid", out var pid))
            {
                return null;
            }
            var verified = root.TryGetProperty("verifiedHostTarget", out var verifiedValue) &&
                verifiedValue.ValueKind == JsonValueKind.Object
                ? verifiedValue
                : default;
            return new(
                String(root, "status") ?? string.Empty,
                String(root, "pluginVersion") ?? string.Empty,
                String(root, "sessionId") ?? string.Empty,
                String(root, "reportId") ?? string.Empty,
                String(root, "qtCompileVersion") ?? string.Empty,
                String(root, "qtRuntimeVersion") ?? string.Empty,
                String(root, "applicationVersion") ?? string.Empty,
                verified.ValueKind == JsonValueKind.Object
                    ? String(verified, "nsightVersion") ?? string.Empty : string.Empty,
                verified.ValueKind == JsonValueKind.Object
                    ? String(verified, "nsightBuild") ?? string.Empty : string.Empty,
                verified.ValueKind == JsonValueKind.Object
                    ? String(verified, "compatibilityProfile") ?? string.Empty : string.Empty,
                String(root, "currentRequestId"),
                String(root, "lastRequestId"),
                String(root, "protocolError"),
                pid);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static JsonDocument? ReadBoundedJson(string path, int maximumBytes)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length is <= 0 || file.Length > maximumBytes)
        {
            return null;
        }
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 32 });
    }

    private static bool ValidateManifest(
        SessionManifest manifest,
        string sessionId,
        string reportId,
        int pid,
        IReadOnlyList<ViewerHostTarget> candidates) =>
        manifest.SessionId == sessionId &&
        manifest.ReportId == reportId &&
        manifest.Pid == pid &&
        manifest.PluginVersion == ViewerProbeRunner.ExpectedBridgeVersion &&
        ViewerHostTargets.MatchVerifiedRuntime(
            candidates,
            manifest.ApplicationVersion,
            manifest.QtRuntimeVersion,
            manifest.QtCompileVersion,
            manifest.VerifiedNsightVersion,
            manifest.VerifiedNsightBuild,
            manifest.CompatibilityProfile) is not null;

    private static bool TryOpenExpectedProcess(
        SessionOwner owner,
        TraceArtifact artifact,
        string viewerPath,
        out Process process)
    {
        process = null!;
        try
        {
            if (owner.SessionKey != SessionKey(artifact, viewerPath) ||
                owner.ReportId != artifact.ReportId ||
                !Path.GetFullPath(owner.ViewerPath).Equals(
                    Path.GetFullPath(viewerPath), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            process = Process.GetProcessById(owner.Pid);
            if (!IsExpectedProcess(
                    process, viewerPath, owner.ProcessStartUtcTicks))
            {
                process.Dispose();
                process = null!;
                return false;
            }
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or
                System.ComponentModel.Win32Exception)
        {
            process?.Dispose();
            process = null!;
            return false;
        }
    }

    private static bool IsExpectedProcess(
        Process process,
        string viewerPath,
        long? expectedStartUtcTicks)
    {
        try
        {
            return !process.HasExited &&
                Path.GetFullPath(process.MainModule!.FileName).Equals(
                    Path.GetFullPath(viewerPath), StringComparison.OrdinalIgnoreCase) &&
                (expectedStartUtcTicks is null ||
                    process.StartTime.ToUniversalTime().Ticks == expectedStartUtcTicks.Value);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
                System.ComponentModel.Win32Exception or ArgumentException)
        {
            return false;
        }
    }

    private static async Task InvalidateAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await Task.WhenAny(process.WaitForExitAsync(), Task.Delay(5_000));
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private static async Task WriteJsonAtomicallyAsync(string path, object value)
    {
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes);
                await stream.FlushAsync();
            }
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            DeleteKnownControlFile(temporaryPath);
        }
    }

    private static string SessionKey(TraceArtifact artifact, string viewerPath)
    {
        var material = string.Join(
            '\n',
            Path.GetFullPath(artifact.FullPath).ToUpperInvariant(),
            artifact.ReportId,
            Path.GetFullPath(viewerPath).ToUpperInvariant(),
            ViewerProbeRunner.ExpectedBridgeVersion);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    private static int SessionIdleTimeoutMs()
    {
        var configured = Environment.GetEnvironmentVariable(
            "NSIGHT_ANALYZER_SESSION_IDLE_TIMEOUT_MS");
        return int.TryParse(configured, out var value)
            ? Math.Clamp(value, MinimumIdleTimeoutMs, MaximumIdleTimeoutMs)
            : DefaultIdleTimeoutMs;
    }

    private static IReadOnlySet<int> SnapshotCrashReporterIds(
        string viewerDirectory,
        int viewerProcessId)
    {
        var expectedPath = Path.Combine(viewerDirectory, "CrashReporter.exe");
        var result = new HashSet<int>();
        foreach (var process in Process.GetProcessesByName("CrashReporter"))
        {
            using (process)
            {
                try
                {
                    if (string.Equals(
                            process.MainModule?.FileName,
                            expectedPath,
                            StringComparison.OrdinalIgnoreCase) &&
                        ViewerProcessRelationships.ParentProcessId(process.Id) ==
                            viewerProcessId)
                    {
                        result.Add(process.Id);
                    }
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException or
                        System.ComponentModel.Win32Exception)
                {
                }
            }
        }
        return result;
    }

    private static async Task<int> CloseUnexpectedCrashReportersAsync(
        string viewerDirectory,
        int viewerProcessId,
        IReadOnlyCollection<int> baselinePids)
    {
        var expectedPath = Path.Combine(viewerDirectory, "CrashReporter.exe");
        var unexpected = new List<Process>();
        foreach (var process in Process.GetProcessesByName("CrashReporter"))
        {
            try
            {
                if (!baselinePids.Contains(process.Id) &&
                    string.Equals(
                        process.MainModule?.FileName,
                        expectedPath,
                        StringComparison.OrdinalIgnoreCase) &&
                    ViewerProcessRelationships.ParentProcessId(process.Id) ==
                        viewerProcessId)
                {
                    unexpected.Add(process);
                }
                else
                {
                    process.Dispose();
                }
            }
            catch (Exception exception) when (
                exception is ArgumentException or InvalidOperationException or
                    System.ComponentModel.Win32Exception)
            {
                process.Dispose();
            }
        }
        foreach (var process in unexpected)
        {
            await CloseCrashReporterAsync(process);
        }
        return unexpected.Count;
    }

    private static async Task CloseBaselineCrashReportersAsync(
        string viewerDirectory,
        int viewerProcessId,
        IReadOnlyCollection<int> baselinePids)
    {
        var expectedPath = Path.Combine(viewerDirectory, "CrashReporter.exe");
        foreach (var pid in baselinePids)
        {
            Process? process = null;
            try
            {
                process = Process.GetProcessById(pid);
                if (!string.Equals(
                        process.MainModule?.FileName,
                        expectedPath,
                        StringComparison.OrdinalIgnoreCase) ||
                    ViewerProcessRelationships.ParentProcessId(process.Id) !=
                        viewerProcessId)
                {
                    process.Dispose();
                    continue;
                }
                await CloseCrashReporterAsync(process);
            }
            catch (Exception exception) when (
                exception is ArgumentException or InvalidOperationException or
                    System.ComponentModel.Win32Exception)
            {
                process?.Dispose();
            }
        }
    }

    private static async Task CloseCrashReporterAsync(Process process)
    {
        using (process)
        {
            try
            {
                if (process.HasExited)
                {
                    return;
                }
                process.CloseMainWindow();
                if (!process.WaitForExit(500))
                {
                    process.Kill();
                    await Task.WhenAny(process.WaitForExitAsync(), Task.Delay(5_000));
                }
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or
                    System.ComponentModel.Win32Exception)
            {
            }
        }
    }

    private static int RemainingMilliseconds(Stopwatch stopwatch, int timeoutMs) =>
        Math.Max(0, timeoutMs - checked((int)Math.Min(
            stopwatch.ElapsedMilliseconds, int.MaxValue)));

    private static void DeleteKnownControlFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string? String(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool TryInt32(JsonElement root, string name, out int result)
    {
        result = 0;
        return root.TryGetProperty(name, out var value) && value.TryGetInt32(out result);
    }

    private static bool TryInt64(JsonElement root, string name, out long result)
    {
        result = 0;
        return root.TryGetProperty(name, out var value) && value.TryGetInt64(out result);
    }

    private static IReadOnlyList<int> Int32Array(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        var result = new List<int>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.TryGetInt32(out var number) && number > 0)
            {
                result.Add(number);
            }
        }
        return result.Distinct().OrderBy(number => number).ToArray();
    }

    private static ViewerSessionInvocationResult Fail(
        ErrorCategory category,
        string code,
        string message,
        string? detail = null) =>
        new(new(category, code, message, detail));

    private static ViewerSessionCloseResult CloseFail(
        ErrorCategory category,
        string code,
        string message,
        string? detail = null) =>
        new(null, new(category, code, message, detail));

    private static EnsureSessionResult EnsureFail(
        ErrorCategory category,
        string code,
        string message,
        string? detail = null) =>
        new(null, null, null, null, new(category, code, message, detail));

    private sealed record LockResult(FileStream? Stream, OperationError? Error);

    private sealed record EnsureSessionResult(
        Process? Process,
        string? SessionId,
        long? ProcessStartUtcTicks,
        IReadOnlyList<int>? BaselineCrashReporterPids,
        OperationError? Error)
    {
        public bool IsSuccess => Process is not null;
    }

    private sealed record SessionOwner(
        string SessionKey,
        string SessionId,
        string ReportId,
        string ViewerPath,
        int Pid,
        long ProcessStartUtcTicks,
        IReadOnlyList<int> BaselineCrashReporterPids);

    private sealed record SessionManifest(
        string Status,
        string PluginVersion,
        string SessionId,
        string ReportId,
        string QtCompileVersion,
        string QtRuntimeVersion,
        string ApplicationVersion,
        string VerifiedNsightVersion,
        string VerifiedNsightBuild,
        string CompatibilityProfile,
        string? CurrentRequestId,
        string? LastRequestId,
        string? ProtocolError,
        int Pid);
}
