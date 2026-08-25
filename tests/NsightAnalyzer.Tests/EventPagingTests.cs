using NsightAnalyzer.Adapters.NsightViewer2026_2;
using Xunit;

namespace NsightAnalyzer.Tests;

/// <summary>
/// Every collection must expose exact paging identity: cursor, limit, totals,
/// truncation and a next cursor that closes against the declared total.
/// </summary>
public sealed class EventPagingTests
{
    [Fact]
    public void EventPageReportsExactPagingIdentity()
    {
        using var bridge = FixtureBridge.Load("events-page0.json");

        var value = BridgeProjection.ProjectEvents(bridge.RootElement, 0, 100);
        var page = value.Events;

        Assert.Equal(0, page.Cursor);
        Assert.Equal(100, page.Limit);
        Assert.Equal(4951, page.TotalCount);
        Assert.Equal(100, page.ReturnedCount);
        Assert.Equal(100, page.Items.Count);
        Assert.True(page.Truncated);
        Assert.Equal(100, page.NextCursor);
    }

    [Fact]
    public void EventKeysArePreorderContiguousWithinThePage()
    {
        using var bridge = FixtureBridge.Load("events-page0.json");

        var items = BridgeProjection.ProjectEvents(bridge.RootElement, 0, 100).Events.Items;

        for (var index = 0; index < items.Count; index++)
        {
            Assert.Equal(index, items[index].Key.PreorderOrdinal);
        }
    }

    [Fact]
    public void EventTreePathDepthMatchesReportedDepth()
    {
        using var bridge = FixtureBridge.Load("events-page0.json");

        var items = BridgeProjection.ProjectEvents(bridge.RootElement, 0, 100).Events.Items;

        // treePath is the row-index chain from the root, so its length is always
        // one greater than the zero-based depth.
        Assert.All(items, fact =>
            Assert.Equal(fact.Depth + 1, fact.Key.TreePath.Count));
    }

    [Fact]
    public void RequestedPageMustMatchTheBridgePage()
    {
        using var bridge = FixtureBridge.Load("events-page0.json");

        // The fixture is offset 0 / limit 100; asking for a different window
        // must fail loudly rather than silently return the wrong page.
        Assert.Throws<BridgeSchemaException>(() =>
            BridgeProjection.ProjectEvents(bridge.RootElement, 100, 100));
    }
}
