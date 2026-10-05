using System.Text.Json;
using System.Text.Json.Nodes;
using NsightAnalyzer.Adapters.NsightViewer2026_2;
using Xunit;

namespace NsightAnalyzer.Tests;

public sealed class MetricSnapshotTests
{
    [Fact]
    public void SnapshotCannotOutliveItsExactViewerInstanceOrTrace()
    {
        using var fixture = SessionFixture();
        var live = new ViewerSessionIdentity("fixture-session", "session-a", 123, 987);
        var snapshot = RangeMetricSnapshotReader.MakeSnapshot(live, "trace-a", "ordinal:1773", fixture.RootElement);
        Assert.True(RangeMetricSnapshotReader.Matches(snapshot, live, "trace-a", "ordinal:1773"));
        Assert.False(RangeMetricSnapshotReader.Matches(snapshot, null, "trace-a", "ordinal:1773"));
        Assert.False(RangeMetricSnapshotReader.Matches(snapshot, live with { SessionId = "session-b" }, "trace-a", "ordinal:1773"));
        Assert.False(RangeMetricSnapshotReader.Matches(snapshot, live with { ProcessStartUtcTicks = 988 }, "trace-a", "ordinal:1773"));
        Assert.False(RangeMetricSnapshotReader.Matches(snapshot, live with { Pid = 456 }, "trace-a", "ordinal:1773"));
        Assert.False(RangeMetricSnapshotReader.Matches(snapshot, live, "trace-b", "ordinal:1773"));
        Assert.False(RangeMetricSnapshotReader.Matches(snapshot, live, "trace-a", "ordinal:3901"));
        Assert.False(RangeMetricSnapshotReader.Matches(snapshot with { Producer = "old-build" }, live, "trace-a", "ordinal:1773"));
    }

    [Fact]
    public void CorruptedSnapshotAndUnstableModelAreNotCacheHits()
    {
        using var fixture = SessionFixture();
        var live = new ViewerSessionIdentity("fixture-session", "session-a", 123, 987);
        var snapshot = RangeMetricSnapshotReader.MakeSnapshot(live, "trace-a", "ordinal:1773", fixture.RootElement);
        Assert.False(RangeMetricSnapshotReader.Matches(snapshot with { ContentHash = "incorrect" }, live, "trace-a", "ordinal:1773"));
        var root = JsonNode.Parse(fixture.RootElement.GetRawText())!;
        root["metricsStable"] = false;
        using var unstable = JsonDocument.Parse(root.ToJsonString());
        var other = RangeMetricSnapshotReader.MakeSnapshot(live, "trace-a", "ordinal:1773", unstable.RootElement);
        Assert.False(RangeMetricSnapshotReader.Matches(other, live, "trace-a", "ordinal:1773"));
    }

    [Fact]
    public void HeaderOnlyProofClosesAgainstFullMetricSnapshot()
    {
        using var fixture = SessionFixture();
        using var catalog = SessionFixture(catalog: true);
        Assert.Equal(RangeMetricSnapshotReader.CatalogSignature(fixture.RootElement),
            RangeMetricSnapshotReader.CatalogSignature(catalog.RootElement));
        var root = JsonNode.Parse(catalog.RootElement.GetRawText())!;
        root["metricViews"]![0]!["export"]!["rowCount"] = 999;
        using var changed = JsonDocument.Parse(root.ToJsonString());
        Assert.NotEqual(RangeMetricSnapshotReader.CatalogSignature(fixture.RootElement),
            RangeMetricSnapshotReader.CatalogSignature(changed.RootElement));
    }

    [Fact]
    public void SnapshotSerializationPreservesTypesAndEveryMetricOccurrence()
    {
        using var fixture = SessionFixture();
        var live = new ViewerSessionIdentity("fixture-session", "session-a", 123, 987);
        var snapshot = RangeMetricSnapshotReader.MakeSnapshot(live, "trace-a", "ordinal:1773", fixture.RootElement);
        var restored = JsonSerializer.Deserialize<RangeMetricSnapshotReader.Snapshot>(JsonSerializer.Serialize(snapshot))!;
        Assert.True(RangeMetricSnapshotReader.Matches(restored, live, "trace-a", "ordinal:1773"));
        var expected = BridgeProjection.ProjectRangeMetrics(fixture.RootElement, 1773, null, [], 0, int.MaxValue);
        var observed = BridgeProjection.ProjectRangeMetrics(restored.Raw, 1773, null, [], 0, int.MaxValue);
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(observed));
    }

    [Fact]
    public void AResponseCannotBeAdmittedUnderAnotherViewerProcess()
    {
        using var oldResponse = SessionFixture(pid: 456);
        var live = new ViewerSessionIdentity("fixture-session", "session-a", 123, 987);
        var relabeled = RangeMetricSnapshotReader.MakeSnapshot(live, "trace-a", "ordinal:1773", oldResponse.RootElement);
        Assert.False(RangeMetricSnapshotReader.ResponseMatchesSession(oldResponse.RootElement, live));
        Assert.False(RangeMetricSnapshotReader.Matches(relabeled, live, "trace-a", "ordinal:1773"));
        var root = JsonNode.Parse(oldResponse.RootElement.GetRawText())!;
        root["pid"] = "123";
        using var nonnumeric = JsonDocument.Parse(root.ToJsonString());
        Assert.False(RangeMetricSnapshotReader.ResponseMatchesSession(nonnumeric.RootElement, live));
    }

    [Fact]
    public void ANewCatalogMustBelongToTheSameLiveProcessAsTheSnapshot()
    {
        using var fixture = SessionFixture();
        using var catalog = SessionFixture(catalog: true);
        using var foreignCatalog = SessionFixture(catalog: true, pid: 456);
        var live = new ViewerSessionIdentity("fixture-session", "session-a", 123, 987);
        var snapshot = RangeMetricSnapshotReader.MakeSnapshot(live, "trace-a", "ordinal:1773", fixture.RootElement);
        Assert.True(RangeMetricSnapshotReader.MatchesProof(snapshot, catalog.RootElement, live, "trace-a", "ordinal:1773"));
        Assert.False(RangeMetricSnapshotReader.MatchesProof(snapshot, foreignCatalog.RootElement, live, "trace-a", "ordinal:1773"));
        Assert.False(RangeMetricSnapshotReader.MatchesProof(snapshot, catalog.RootElement, null, "trace-a", "ordinal:1773"));
        Assert.False(RangeMetricSnapshotReader.MatchesProof(snapshot, catalog.RootElement,
            live with { SessionId = "reopened", ProcessStartUtcTicks = 988 }, "trace-a", "ordinal:1773"));
    }

    [Fact]
    public async Task ExhaustedBudgetCannotStartAViewerRequest()
    {
        var artifact = new TraceArtifact("fixture.ngfx-gputrace", "fixture.ngfx-gputrace", 1,
            DateTime.UnixEpoch, "localWeak", "trace-a", null, "report-a");
        var result = await RangeMetricSnapshotReader.ReadAsync(artifact, "missing-viewer.exe", 0,
            1773, null, [], 0, 20);
        Assert.False(result.IsSuccess);
        Assert.Equal("trace.range_metrics_timeout", result.Error!.Code);
    }

    [Fact]
    public async Task DifferentFilesWithEqualMetadataHaveDifferentLocalIdentities()
    {
        var root = Path.Combine(Path.GetTempPath(), "nsa-identity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var a = Path.Combine(root, "a.ngfx-gputrace");
        var b = Path.Combine(root, "b.ngfx-gputrace");
        try
        {
            await File.WriteAllBytesAsync(a, [1, 2, 3]);
            await File.WriteAllBytesAsync(b, [3, 2, 1]);
            var timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(a, timestamp);
            File.SetLastWriteTimeUtc(b, timestamp);
            var first = await TraceArtifactReader.OpenAsync(a, "localWeak", 1000);
            var second = await TraceArtifactReader.OpenAsync(b, "localWeak", 1000);
            Assert.NotEqual(first.Artifact!.ArtifactIdentity, second.Artifact!.ArtifactIdentity);
            Assert.True(TraceArtifactReader.MatchesSnapshot(first.Artifact));
            await File.WriteAllBytesAsync(a, [1, 2, 3, 4]);
            Assert.False(TraceArtifactReader.MatchesSnapshot(first.Artifact));
        }
        finally
        {
            File.Delete(a);
            File.Delete(b);
            Directory.Delete(root);
        }
    }
    private static JsonDocument SessionFixture(bool catalog = false, int pid = 123)
    {
        using var fixture = catalog
            ? FixtureBridge.LoadMetricCatalog("range-metrics-gbufferpass.json")
            : FixtureBridge.Load("range-metrics-gbufferpass.json");
        var root = JsonNode.Parse(fixture.RootElement.GetRawText())!;
        // The source fixture strips process identity. Cache identity tests need
        // an actual numeric PID while retaining every original metric fact.
        root["pid"] = pid;
        root["requestId"] = catalog ? "catalog-request" : "metric-request";
        return JsonDocument.Parse(root.ToJsonString());
    }
}
