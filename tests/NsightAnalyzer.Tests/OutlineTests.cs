using NsightAnalyzer.Adapters.NsightViewer2026_2;
using Xunit;

namespace NsightAnalyzer.Tests;

/// <summary>
/// The outline is the orientation surface: it answers "what ranges exist and
/// how long is each" in one call. Its ordinals must address the same events as
/// trace.events, or every follow-up call would need a second lookup.
/// </summary>
public sealed class OutlineTests
{
    [Fact]
    public void OutlineReturnsOnlyRangeGrainRows()
    {
        using var bridge = FixtureBridge.Load("outline-page0.json");

        var value = BridgeProjection.ProjectOutline(bridge.RootElement, 0, 500);

        Assert.Equal(493, value.Ranges.TotalCount);
        Assert.Equal(4951, value.TotalEventCount);
        Assert.All(value.Ranges.Items, fact =>
            Assert.Contains('-', fact.Key.EventRange!));
    }

    [Fact]
    public void OutlinePreservesTruePreorderOrdinals()
    {
        using var bridge = FixtureBridge.Load("outline-page0.json");

        var items = BridgeProjection.ProjectOutline(bridge.RootElement, 0, 500).Ranges.Items;

        // Filtered rows keep their position in the whole tree rather than being
        // renumbered, so an outline ordinal is directly usable as an EventKey.
        var ordinals = items.Select(fact => fact.Key.PreorderOrdinal!.Value).ToArray();
        Assert.Equal(ordinals.OrderBy(value => value), ordinals);
        Assert.True(ordinals.Distinct().Count() == ordinals.Length);
        Assert.True(ordinals[^1] > items.Count, "Ordinals were renumbered to the filtered sequence.");
    }

    [Fact]
    public void OutlineLabelsGrainWithoutDroppingEitherKind()
    {
        using var bridge = FixtureBridge.Load("outline-page0.json");

        var items = BridgeProjection.ProjectOutline(bridge.RootElement, 0, 500).Ranges.Items;

        Assert.All(items, fact =>
            Assert.True(fact.Grain is "container" or "marker", fact.Grain));
        Assert.Contains(items, fact => fact.Grain == "container");
        Assert.Contains(items, fact => fact.Grain == "marker");
    }

    [Fact]
    public void OutlineRequiresTheBridgeFilter()
    {
        // An unfiltered capture would silently look like a complete outline
        // while actually containing every draw call.
        using var bridge = FixtureBridge.Load("events-page0.json");

        Assert.Throws<BridgeSchemaException>(() =>
            BridgeProjection.ProjectOutline(bridge.RootElement, 0, 100));
    }
}
