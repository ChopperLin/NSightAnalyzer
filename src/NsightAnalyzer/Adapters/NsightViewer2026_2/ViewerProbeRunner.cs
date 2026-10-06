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

internal enum ViewerProbeCachePolicy
{
    None,
    RangeShadersV1,
}

internal sealed class ViewerProbeRun : IDisposable
{
    public ViewerProbeRun(
        JsonDocument document,
        string outputPath,
        TimeSpan duration,
        string mode,
        ViewerHostTarget target)
    {
        Document = document;
        OutputPath = outputPath;
        Duration = duration;
        Mode = mode;
        Target = target;
    }

    public JsonDocument Document { get; }
    public string OutputPath { get; }
    public TimeSpan Duration { get; }
    public string Mode { get; }
    public ViewerHostTarget Target { get; }

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
    public const string SupportLevel = "unsupportedVersionPinned";
    public const string ExpectedBridgeVersion = "probe-0.52";

    private const long MaximumRawOutputBytes = 128L * 1024 * 1024;
    private static readonly HashSet<string> ProductSettingNames =
    [
        "ACTION_MATCH_MODE",
        "ACTION_SETTLE_MIN_POLLS",
        "ACTION_TRIGGER_ASYNC",
        "ACTION_TRIGGER_OCCURRENCE",
        "ACTION_TRIGGER_TEXT_MATCH",
        "ACTIVATE_PANEL",
        "ACTIVATE_PANEL_VIA_BUTTON",
        "CLOSE_MODAL_BEFORE_QUIT",
        "COMBO_ANCESTRY_CLASS_MATCH",
        "COMBO_MATCH_MODE",
        "COMBO_OBJECT_MATCH",
        "COMBO_SELECT_FROM_MODEL_SELECTION",
        "COMBO_SELECT_MATCH",
        "COMBO_SELECT_MATCH_MODE",
        "COMBO_SELECT_OCCURRENCE",
        "COMBO_SELECT_SETTLE_MIN_POLLS",
        "COMBO_SELECT_TRIGGER",
        "DIALOG_AUTO_PATH",
        "EVENT_FILTER_COLUMN",
        "EVENT_STABLE_SAMPLES",
        "EVENT_FILTER_CONTAINS",
        "EVENT_GRAIN_COLUMN",
        "EVENT_GRAIN_MODE",
        "EVENT_GRAIN_PREFIXES",
        "EVENT_GRAIN_SUBSTRINGS",
        "EVENT_INCLUDE_ANCESTORS",
        "EVENT_NAME_COLUMN",
        "EVENT_NAME_CONTAINS",
        "EVENT_NAME_EXACT",
        "EVENT_NAME_MODE",
        "EVENT_NAME_PREFIXES",
        "EVENT_NAME_SUBSTRINGS",
        "EVENT_INCLUDE_ITEM_DATA",
        "EVENT_LIMIT",
        "EVENT_OFFSET",
        "EVENT_ORDINAL",
        "EVENT_PATH",
        "EVENT_SORT_DURATION_COLUMN",
        "EVENT_WITHIN_ORDINAL",
        "EXTERNAL_KILL_ON_TIMEOUT",
        "INVOKE_CLASS_MATCH",
        "INVOKE_MATCH_MODE",
        "INVOKE_METHOD",
        "INVOKE_OBJECT_MATCH",
        "INVOKE_OCCURRENCE",
        "INVOKE_SETTLE_MIN_POLLS",
        "MAX_DEPTH",
        "METRIC_LIMIT",
        "METRIC_CATALOG_ONLY",
        "METRIC_SETTLE_MAX_POLLS",
        "METRIC_SETTLE_MIN_POLLS",
        "MODEL_ATTACHED_VIEW_OBJECT_MATCH",
        "MODEL_CLASS_MATCH",
        "MODEL_COLUMNS",
        "MODEL_FLAT",
        "MODEL_LIMIT",
        "MODEL_MATCH_MODE",
        "MODEL_OBJECT_MATCH",
        "MODEL_REQUIRED_MIN_COUNT",
        "MODEL_SELECT_COLUMN",
        "MODEL_SELECT_DEFER_PROVIDER_VERIFY_UNTIL_COMBO",
        "MODEL_SELECT_MATCH",
        "MODEL_SELECT_MATCH_MODE",
        "MODEL_SELECT_OCCURRENCE",
        "MODEL_SELECT_PREPARE_PANEL",
        "MODEL_SELECT_PREPARE_PANEL_SETTLE_MIN_POLLS",
        "MODEL_SELECT_PROVIDER_STABLE_MIN_POLLS",
        "MODEL_SELECT_SETTLE_MIN_POLLS",
        "MODEL_SELECT_VIEW_OBJECT",
        "MODEL_SETTLE_MIN_POLLS",
        "PANEL_SETTLE_MIN_POLLS",
        "POLL_INTERVAL_MS",
        "SELECTION_BASELINE_METRIC_MIN_COUNT",
        "SELECTION_BASELINE_METRIC_STABLE_MIN_POLLS",
    ];

    public static async Task<ViewerProbeRunResult> RunAsync(
        TraceArtifact artifact,
        ViewerProbeMode mode,
        string expectedSchema,
        IReadOnlyDictionary<string, string> settings,
        string? viewerPathOverride,
        int timeoutMs,
        ViewerProbeCachePolicy cachePolicy = ViewerProbeCachePolicy.None)
    {
        var viewerPath = ViewerHostTargets.ResolveViewerPath(viewerPathOverride);
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
        var candidates = ViewerHostTargets.FromFileVersion(fileVersion);
        if (candidates.Count == 0)
        {
            return Fail(
                ErrorCategory.AdapterMismatch,
                "viewer.version_mismatch",
                "The adapter only supports explicitly verified Nsight Graphics Viewer builds.",
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

        string runRoot;
        string sessionRoot;
        string runDirectory;
        string outputPath;
        try
        {
            runRoot = ViewerSessionTransport.ResolveRunRoot();
            sessionRoot = ViewerSessionTransport.ResolveSessionRoot();
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
        var normalizedSettings = new Dictionary<string, string>(StringComparer.Ordinal);
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
            normalizedSettings.Add(setting.Key, settingValue);
        }

        var invoked = await ViewerSessionTransport.ExecuteAsync(
            artifact,
            viewerPath,
            viewerDirectory,
            ModeName(mode),
            expectedSchema,
            normalizedSettings,
            runRoot,
            sessionRoot,
            runDirectory,
            outputPath,
            requestId,
            timeoutMs,
            CachePolicyName(cachePolicy),
            candidates);
        stopwatch.Stop();
        if (!invoked.IsSuccess)
        {
            return new(null, invoked.Error);
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
            expectedSchema,
            candidates,
            out var target);
        if (validationError is not null)
        {
            document.Dispose();
            return new(null, validationError);
        }

        return new(
            new ViewerProbeRun(document, outputPath, stopwatch.Elapsed, ModeName(mode), target!),
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
            run.Target.ProductVersion,
            run.Target.ProductBuild,
            run.Target.ProductSku,
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
        string expectedSchema,
        IReadOnlyList<ViewerHostTarget> candidates,
        out ViewerHostTarget? target)
    {
        target = null;
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
        var applicationVersion = TryString(root, "applicationVersion", out var observedApplication)
            ? observedApplication : null;
        var qtVersion = TryString(root, "qtRuntimeVersion", out var observedQt)
            ? observedQt : null;
        var qtCompileVersion = TryString(root, "qtCompileVersion", out var observedCompileQt)
            ? observedCompileQt : null;
        var applicationCandidates = candidates.Where(candidate => string.Equals(
            applicationVersion, candidate.ApplicationVersion, StringComparison.Ordinal)).ToArray();
        if (applicationCandidates.Length == 0)
        {
            return Error(
                ErrorCategory.AdapterMismatch,
                "viewer.build_mismatch",
                "The decoding Viewer version/build/SKU does not match the exact compatibility entry.",
                $"expected={string.Join(" | ", candidates.Select(candidate => candidate.ApplicationVersion))}; " +
                $"observed={applicationVersion ?? "missing"}");
        }
        var runtimeTarget = applicationCandidates.SingleOrDefault(candidate =>
            ViewerHostTargets.MatchesRuntime(
                candidate, applicationVersion, qtVersion, qtCompileVersion));
        if (runtimeTarget is null)
        {
            return Error(
                ErrorCategory.AdapterMismatch,
                "viewer.qt_mismatch",
                "The decoding Viewer runtime/bridge Qt pairing does not match the exact compatibility entry.",
                $"runtimeExpected={string.Join(" | ", applicationCandidates.Select(candidate => candidate.QtRuntimeVersion))}; " +
                $"runtimeObserved={qtVersion ?? "missing"}; compileExpected=" +
                $"{string.Join(" | ", applicationCandidates.Select(candidate => candidate.QtCompileVersion))}; " +
                $"compileObserved={qtCompileVersion ?? "missing"}");
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
            verifiedVersion != runtimeTarget.NsightVersion ||
            !TryString(verified, "nsightBuild", out var verifiedBuild) ||
            verifiedBuild != runtimeTarget.ProductBuild ||
            !TryString(verified, "compatibilityProfile", out var verifiedProfile) ||
            verifiedProfile != runtimeTarget.CompatibilityProfile)
        {
            return Error(
                ErrorCategory.AdapterMismatch,
                "viewer.host_unverified",
                "The bridge did not verify the expected Viewer host.");
        }
        target = runtimeTarget;
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

    private static string CachePolicyName(ViewerProbeCachePolicy policy) => policy switch
    {
        ViewerProbeCachePolicy.None => "none",
        ViewerProbeCachePolicy.RangeShadersV1 => "range-shaders-v1",
        _ => throw new ArgumentOutOfRangeException(nameof(policy)),
    };

    private static bool IsSafeSettingName(string name) =>
        ProductSettingNames.Contains(name);

    internal static void RemoveHighConfidenceSecrets(ProcessStartInfo startInfo)
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
