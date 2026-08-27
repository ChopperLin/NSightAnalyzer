using NsightAnalyzer.Adapters.NsightViewer2026_2;
using Xunit;

namespace NsightAnalyzer.Tests;

public sealed class EventCandidateProjectionTests
{
    [Fact]
    public void CandidatesRetainExactKeysAndPreorder()
    {
        using var bridge = FixtureBridge.LoadEventCandidates(
            "event-name-matches-pass01.json");

        var value = BridgeProjection.ProjectEventCandidates(
            bridge.RootElement, 0, 500, "Pass01", null);

        Assert.Null(value.WithinScope);
        Assert.Equal(4951, value.TotalEventCount);
        Assert.All(value.Events.Items, candidate =>
            Assert.Contains("Pass01", candidate.Key.Description));
        Assert.Equal(
            value.Events.Items.Select(item => item.Key.PreorderOrdinal).Order(),
            value.Events.Items.Select(item => item.Key.PreorderOrdinal));
    }
}

public sealed class RangeCandidateProjectionTests
{
    [Fact]
    public void CandidatesAreCompactAndDurationOrdered()
    {
        using var bridge = FixtureBridge.LoadDurationOrderedRanges("outline-page0.json");

        var value = BridgeProjection.ProjectRangeCandidates(
            bridge.RootElement, 0, 500, null, EventGrain.All, null);

        Assert.Equal(4951, value.TotalEventCount);
        Assert.Equal(493, value.Ranges.TotalCount);
        Assert.All(value.Ranges.Items, candidate =>
            Assert.Contains('-', candidate.Range.Key.EventRange!));
        var parsed = value.Ranges.Items
            .Where(candidate => candidate.Duration.Milliseconds is not null)
            .Select(candidate => candidate.Duration.Milliseconds!.Value)
            .ToArray();
        Assert.Equal(parsed.OrderDescending(), parsed);
    }
}

public sealed class RangeMetricCatalogProjectionTests
{
    [Fact]
    public void CatalogReturnsTableIdentityWithoutMetricValues()
    {
        using var bridge = FixtureBridge.LoadMetricCatalog(
            "range-metrics-gbufferpass.json");

        var value = BridgeProjection.ProjectRangeMetricCatalog(
            bridge.RootElement, 1773, null, 0, 100);

        Assert.Equal(88, value.Tables.TotalCount);
        Assert.All(value.Tables.Items, table =>
        {
            Assert.True(table.RowCount > 0);
            Assert.Equal("available", table.Availability);
            Assert.NotEmpty(table.Columns);
        });
        Assert.Contains(
            value.Tables.Items,
            table => table.Name == "SM Register Occupancy");
    }
}

public sealed class ShaderProfileProjectionTests
{
    [Fact]
    public void ProfileReturnsOneExactStageHashOccurrence()
    {
        using var bridge = FixtureBridge.Load("range-shaders-gbufferpass.json");

        var value = BridgeProjection.ProjectShaderProfile(
            bridge.RootElement,
            null,
            "0.2.12.71.0",
            "Compute",
            "0x0000000000000000",
            0);

        Assert.Equal("Compute", value.Shader.Key.Stage);
        Assert.Equal("0x0000000000000000", value.Shader.Key.Hash);
        Assert.Equal(0, value.Shader.Key.HashOccurrence);
    }

    [Fact]
    public void ProfileDoesNotAcceptAnotherStageWithTheSameHash()
    {
        using var bridge = FixtureBridge.Load("range-shaders-gbufferpass.json");

        Assert.Throws<BridgeFactNotFoundException>(() =>
            BridgeProjection.ProjectShaderProfile(
                bridge.RootElement,
                null,
                "0.2.12.71.0",
                "NotAStage",
                "0x0000000000000000",
                0));
    }
}
