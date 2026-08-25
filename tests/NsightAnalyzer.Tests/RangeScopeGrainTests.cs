using NsightAnalyzer.Adapters.NsightViewer2026_2;
using Xunit;

namespace NsightAnalyzer.Tests;

/// <summary>
/// Capability matrix SCP-002: a single draw/dispatch/barrier scope has no
/// reliable PC-sampling denominator, so range-grain fact families must refuse
/// it rather than return a plausible value.
/// </summary>
public sealed class RangeScopeGrainTests
{
    private const int GBufferPassOrdinal = 1773;
    private const int ClearRenderTargetViewOrdinal = 1774;
    private const string GBufferPassPath = "0.2.12.71.0";
    private static readonly int[] GBufferPassTreePath = [0, 2, 12, 71, 0];

    [Fact]
    public void RangeMetricsRefusesSingleCommandScope()
    {
        using var bridge = FixtureBridge.Load("range-metrics-draw-scope.json");

        var exception = Assert.Throws<BridgeScopeUnsupportedException>(() =>
            BridgeProjection.ProjectRangeMetrics(
                bridge.RootElement,
                ClearRenderTargetViewOrdinal,
                null,
                [],
                0,
                100));

        Assert.Equal("trace.unsupported_draw_scope", exception.Code);
        Assert.Contains("eventRange=1606", exception.Detail);
    }

    [Fact]
    public void RangeMetricsAcceptsPassScope()
    {
        using var bridge = FixtureBridge.Load("range-metrics-gbufferpass.json");

        var value = BridgeProjection.ProjectRangeMetrics(
            bridge.RootElement, GBufferPassOrdinal, null, [], 0, 500);

        Assert.Equal(88, value.TableCount);
        Assert.Equal(409, value.RowCount);
        Assert.Equal(801, value.Metrics.TotalCount);
    }

    [Fact]
    public void RangeInstructionMixAcceptsPassScope()
    {
        using var bridge = FixtureBridge.Load("range-instruction-mix-gbufferpass.json");

        var value = BridgeProjection.ProjectRangeInstructionMix(
            bridge.RootElement, GBufferPassOrdinal, null, 0, 500);

        Assert.Equal(56, value.Instructions.TotalCount);
    }

    [Fact]
    public void RangeShadersAcceptsPassScope()
    {
        using var bridge = FixtureBridge.Load("range-shaders-gbufferpass.json");

        // This capture was taken with a path selector rather than an ordinal,
        // which exercises the other half of ProjectVerifiedScope.
        var value = BridgeProjection.ProjectRangeShaders(
            bridge.RootElement, null, GBufferPassPath, null, null, 0, 500);

        Assert.NotEmpty(value.Shaders.Items);
        Assert.Equal(GBufferPassTreePath, value.Scope.TreePath);
    }
}

/// <summary>
/// The Viewer's own instruction-mix row order is not stable for categories tied
/// on sample count: the same range has been observed emitting them in either
/// order across runs. The projection therefore imposes its own order.
/// </summary>
public sealed class InstructionMixOrderingTests
{
    [Fact]
    public void CategoriesAreOrderedByTheirOwnIdentity()
    {
        using var bridge = FixtureBridge.Load("range-instruction-mix-gbufferpass.json");

        var items = BridgeProjection
            .ProjectRangeInstructionMix(bridge.RootElement, 1773, null, 0, 500)
            .Instructions.Items;

        var expected = items
            .OrderBy(fact => fact.Pipe, StringComparer.Ordinal)
            .ThenBy(fact => fact.Family, StringComparer.Ordinal)
            .ThenBy(fact => fact.Operation, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expected.Select(fact => fact.Pipe), items.Select(fact => fact.Pipe));
        Assert.Equal(expected.Select(fact => fact.Family), items.Select(fact => fact.Family));
    }

    [Fact]
    public void SourceOrdinalIsAStableCursorIntoThatOrder()
    {
        using var bridge = FixtureBridge.Load("range-instruction-mix-gbufferpass.json");

        var items = BridgeProjection
            .ProjectRangeInstructionMix(bridge.RootElement, 1773, null, 0, 500)
            .Instructions.Items;

        Assert.Equal(Enumerable.Range(0, items.Count), items.Select(fact => fact.SourceOrdinal));
    }
}
