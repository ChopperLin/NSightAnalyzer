using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NsightAnalyzer.Contracts;
using NsightAnalyzer.Operations;

namespace NsightAnalyzer.Adapters.NsightViewer2026_2;

// A transport optimization for the one pinned decoder. A cache hit still makes
// a fresh, self-contained catalog request to establish the exact scope, wait for
// its models, and verify the live session. No source/action/counter data is cached.
internal static class RangeMetricSnapshotReader
{
    private const string SnapshotSchema = "NsightAnalyzerRangeMetricSnapshotV1";
    private const int MaximumEntryBytes = 8 * 1024 * 1024;
    private const int MaximumEntries = 4;
    private static readonly string ProducerIdentity = typeof(RangeMetricSnapshotReader).Module.ModuleVersionId.ToString("N");
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    internal sealed record Snapshot(
        string Schema, string Producer, ViewerSessionIdentity Session,
        string ArtifactIdentity, string Selector, string ContentHash, JsonElement Raw);

    public static async Task<OperationResult> ReadAsync(
        TraceArtifact artifact, string? viewerPathOverride, int timeoutMs,
        int? eventOrdinal, string? eventPath, IReadOnlyList<string> tables,
        int cursor, int limit)
    {
        var timer = Stopwatch.StartNew();
        if (!TryRemaining(timer, timeoutMs, out _)) return TimedOut();
        var viewer = ViewerHostTargets.ResolveViewerPath(viewerPathOverride);
        var selector = eventOrdinal is not null ? $"ordinal:{eventOrdinal}" : $"path:{eventPath}";
        var session = ViewerSessionTransport.TryGetLiveIdentity(artifact, viewer);
        var snapshot = session is null ? null : TryLoad(session, artifact.ArtifactIdentity, selector);
        if (snapshot is not null)
        {
            var settings = TraceEventParametersOperation.ScopedSettings(eventOrdinal, eventPath);
            settings["POLL_INTERVAL_MS"] = "100";
            settings["METRIC_CATALOG_ONLY"] = "1";
            if (!TryRemaining(timer, timeoutMs, out var proofRemaining)) return TimedOut();
            var proof = await ViewerProbeRunner.RunAsync(artifact, ViewerProbeMode.SelectionMetricsExport,
                "NsightSolidProbeSelectionMetricsV1", settings, viewer, proofRemaining);
            if (!proof.IsSuccess) return OperationResult.Failure(proof.Error!);
            using var run = proof.Run!;
            try
            {
                var proofRaw = run.Document.RootElement;
                var catalog = BridgeProjection.ProjectRangeMetricCatalog(proofRaw,
                    eventOrdinal, eventPath, 0, int.MaxValue);
                var live = LiveForResponse(artifact, viewer, proofRaw);
                if (MatchesProof(snapshot, proofRaw, live, artifact.ArtifactIdentity, selector))
                {
                    var value = BridgeProjection.ProjectRangeMetrics(snapshot.Raw,
                        eventOrdinal, eventPath, tables, cursor, limit);
                    if (!SameScope(catalog.Scope, value.Scope))
                    {
                        return OperationResult.Failure(ErrorCategory.AdapterMismatch,
                            "viewer.metric_snapshot_scope_mismatch", "The metric snapshot and fresh selection have different scopes.");
                    }
                    if (!TraceArtifactReader.MatchesSnapshot(artifact)) return ChangedFile();
                    WriteReceipt(run.OutputPath, true, snapshot.ContentHash);
                    return OperationResult.Success(value,
                        [ViewerProbeRunner.CreateProvenance(run, artifact, "trace.range-metrics/v1")],
                        OperationSupport.ViewerWarnings);
                }
            }
            catch (BridgeFactNotFoundException exception) { return NotFound(exception); }
            catch (BridgeScopeUnsupportedException exception) { return OperationSupport.ScopeUnsupportedFailure(exception); }
            catch (BridgeFactUnavailableException exception) { return Unavailable(exception); }
            catch (BridgeSchemaException exception) { return OperationSupport.ProjectionFailure(exception); }
        }

        var freshSettings = TraceEventParametersOperation.ScopedSettings(eventOrdinal, eventPath);
        freshSettings["POLL_INTERVAL_MS"] = "100";
        if (!TryRemaining(timer, timeoutMs, out var freshRemaining)) return TimedOut();
        var probe = await ViewerProbeRunner.RunAsync(artifact, ViewerProbeMode.SelectionMetricsExport,
            "NsightSolidProbeSelectionMetricsV1", freshSettings, viewer, freshRemaining);
        if (!probe.IsSuccess) return OperationResult.Failure(probe.Error!);
        using var freshRun = probe.Run!;
        try
        {
            var raw = freshRun.Document.RootElement;
            var value = BridgeProjection.ProjectRangeMetrics(raw, eventOrdinal, eventPath, tables, cursor, limit);
            if (!TraceArtifactReader.MatchesSnapshot(artifact)) return ChangedFile();
            var live = LiveForResponse(artifact, viewer, raw);
            // Admission requires the complete unfiltered projection, in addition
            // to the bridge's own stability/selection validation.
            var complete = BridgeProjection.ProjectRangeMetrics(raw, eventOrdinal, eventPath, [], 0, int.MaxValue);
            if (ResponseMatchesSession(raw, live) && !complete.Metrics.Truncated && IsStable(raw))
            {
                TryStore(live!, artifact.ArtifactIdentity, selector, raw);
            }
            WriteReceipt(freshRun.OutputPath, false, null);
            return OperationResult.Success(value,
                [ViewerProbeRunner.CreateProvenance(freshRun, artifact, "trace.range-metrics/v1")],
                OperationSupport.ViewerWarnings);
        }
        catch (BridgeFactNotFoundException exception) { return NotFound(exception); }
        catch (BridgeScopeUnsupportedException exception) { return OperationSupport.ScopeUnsupportedFailure(exception); }
        catch (BridgeFactUnavailableException exception) { return Unavailable(exception); }
        catch (BridgeSchemaException exception) { return OperationSupport.ProjectionFailure(exception); }
    }

    internal static bool Matches(Snapshot snapshot, ViewerSessionIdentity? live,
        string artifactIdentity, string selector) =>
        live is not null && snapshot.Schema == SnapshotSchema && snapshot.Producer == ProducerIdentity &&
        snapshot.Session == live && snapshot.ArtifactIdentity == artifactIdentity && snapshot.Selector == selector &&
        ResponseMatchesSession(snapshot.Raw, live) &&
        snapshot.ContentHash == Hash(JsonSerializer.Serialize(snapshot.Raw)) && IsStable(snapshot.Raw);

    internal static bool MatchesProof(Snapshot snapshot, JsonElement proof, ViewerSessionIdentity? live,
        string artifactIdentity, string selector) =>
        Matches(snapshot, live, artifactIdentity, selector) && ResponseMatchesSession(proof, live) &&
        IsStable(proof) && CatalogSignature(snapshot.Raw) == CatalogSignature(proof);

    internal static bool ResponseMatchesSession(JsonElement raw, ViewerSessionIdentity? live) =>
        live is not null && raw.ValueKind == JsonValueKind.Object &&
        raw.TryGetProperty("pid", out var pid) && pid.ValueKind == JsonValueKind.Number &&
        pid.TryGetInt32(out var value) && value > 0 && value == live.Pid;

    private static ViewerSessionIdentity? LiveForResponse(TraceArtifact artifact, string viewer, JsonElement raw) =>
        raw.TryGetProperty("requestId", out var request) && request.ValueKind == JsonValueKind.String &&
        !string.IsNullOrEmpty(request.GetString())
            ? ViewerSessionTransport.TryGetLiveIdentity(artifact, viewer, request.GetString()) : null;

    private static bool IsStable(JsonElement root) =>
        root.TryGetProperty("status", out var status) && status.GetString() == "complete" &&
        root.TryGetProperty("metricsStable", out var stable) && stable.ValueKind == JsonValueKind.True &&
        root.TryGetProperty("selectionMatchesTarget", out var selected) && selected.ValueKind == JsonValueKind.True &&
        (!root.TryGetProperty("settleTimedOut", out var timedOut) || timedOut.ValueKind == JsonValueKind.False);

    private static Snapshot? TryLoad(ViewerSessionIdentity live, string artifact, string selector)
    {
        try
        {
            var path = SnapshotPath(live, selector);
            var file = new FileInfo(path);
            if (!file.Exists || file.Length is <= 0 or > MaximumEntryBytes) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            var snapshot = JsonSerializer.Deserialize<Snapshot>(stream, JsonOptions);
            return snapshot is not null && Matches(snapshot, live, artifact, selector) ? snapshot : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    internal static Snapshot MakeSnapshot(ViewerSessionIdentity live, string artifact, string selector, JsonElement raw) =>
        new(SnapshotSchema, ProducerIdentity, live, artifact, selector,
            Hash(JsonSerializer.Serialize(raw)), raw.Clone());

    private static void TryStore(ViewerSessionIdentity live, string artifact, string selector, JsonElement raw)
    {
        try
        {
            var path = SnapshotPath(live, selector);
            var directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(MakeSnapshot(live, artifact, selector, raw), JsonOptions);
            if (bytes.Length > MaximumEntryBytes) return;
            // No waiting for a cache writer: cache contention only loses a cache
            // admission, never correctness or the caller's runtime budget.
            using var writeLock = new FileStream(Path.Combine(directory, "write.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var files = new DirectoryInfo(directory).GetFiles("*.json", SearchOption.TopDirectoryOnly)
                .Where(file => !file.FullName.Equals(path, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(file => file.LastWriteTimeUtc).ToArray();
            foreach (var old in files.Skip(MaximumEntries - 1)) old.Delete();
            var temporary = path + ".tmp";
            try
            {
                File.WriteAllBytes(temporary, bytes);
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A read-only or contended cache cannot fail an otherwise valid atom.
        }
    }

    internal static string CatalogSignature(JsonElement raw)
    {
        var tables = new List<string>();
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var view in raw.GetProperty("metricViews").EnumerateArray())
        {
            var export = view.GetProperty("export");
            var headers = export.GetProperty("headers").EnumerateArray()
                .Select(header => (Column: header.GetProperty("column").GetInt32(), Text: header.GetProperty("display").GetString()))
                .OrderBy(header => header.Column).ToArray();
            var name = headers.Single(header => header.Column == 0).Text ?? "";
            var occurrence = occurrences.GetValueOrDefault(name);
            occurrences[name] = occurrence + 1;
            var rowCount = export.TryGetProperty("rowCount", out var rows) ? rows.GetInt32() : export.GetProperty("totalCount").GetInt32();
            tables.Add(JsonSerializer.Serialize(new
            {
                name,
                occurrence,
                rowCount,
                columns = headers.Select(header => new { header.Column, header.Text }).ToArray()
            }));
        }
        return Hash(string.Join('\n', tables.Order(StringComparer.Ordinal)));
    }

    private static string SnapshotPath(ViewerSessionIdentity live, string selector) =>
        Path.Combine(live.Directory, "metric-snapshots", Hash(selector) + ".json");

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static bool TryRemaining(Stopwatch timer, int maximum, out int remaining)
    {
        remaining = (int)Math.Max(0L, (long)maximum - timer.ElapsedMilliseconds);
        return remaining > 0;
    }
    private static bool SameScope(EventKey left, EventKey right) => left.PreorderOrdinal == right.PreorderOrdinal &&
        left.TreePath.SequenceEqual(right.TreePath) && left.EventRange == right.EventRange && left.Description == right.Description;
    private static OperationResult ChangedFile() => OperationResult.Failure(ErrorCategory.Unavailable, "trace.file_changed", "The report changed during metric retrieval.");
    private static OperationResult TimedOut() => OperationResult.Failure(ErrorCategory.Timeout,
        "trace.range_metrics_timeout", "The metric request exhausted its total deadline.");
    private static OperationResult NotFound(BridgeFactNotFoundException error) => OperationResult.Failure(ErrorCategory.NotFound, error.Code, error.Message);
    private static OperationResult Unavailable(BridgeFactUnavailableException error) => OperationResult.Failure(ErrorCategory.Unavailable, error.Code, error.Message, error.Detail);

    private static void WriteReceipt(string outputPath, bool hit, string? contentHash)
    {
        try
        {
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(outputPath)!, "metric-snapshot.json"),
                JsonSerializer.Serialize(new
                {
                    schema = SnapshotSchema,
                    hit,
                    contentHash,
                    validation = "freshMetricCatalogAndExactLiveSession"
                }));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
}
