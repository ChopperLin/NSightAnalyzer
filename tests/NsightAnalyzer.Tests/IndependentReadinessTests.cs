using System.Text.Json;
using System.Text.Json.Nodes;
using NsightAnalyzer.Adapters.NsightViewer2026_2;
using NsightAnalyzer.Operations;
using Xunit;

namespace NsightAnalyzer.Tests;

public sealed class IndependentReadinessTests
{
    [Fact]
    public void EventAndShaderRequestsDoNotWaitForWarpMetrics()
    {
        Assert.Equal("0", TraceEventParametersOperation.ScopedSettings(823, null)["REQUIRE_METRIC_VIEWS"]);
        Assert.Equal("0", TraceRangeShadersOperation.BridgeSettings(823, null)["REQUIRE_METRIC_VIEWS"]);
        var metricSettings = TraceEventParametersOperation.ScopedSettings(823, null, requireMetricViews: true);
        Assert.Equal("1", metricSettings["REQUIRE_METRIC_VIEWS"]);
        Assert.Equal("1", metricSettings["SELECTION_BASELINE_METRIC_MIN_COUNT"]);
    }

    [Fact]
    public void ShaderFactsRemainCompleteWithoutUnrequestedMetricTables()
    {
        using var fixture = FixtureBridge.Load("range-shaders-gbufferpass.json");
        var expected = BridgeProjection.ProjectRangeShaders(fixture.RootElement, null, "0.2.12.71.0", null, null, 0, int.MaxValue);
        var root = JsonNode.Parse(fixture.RootElement.GetRawText())!.AsObject();
        root.Remove("metricViews");
        root.Remove("baselineMetricViews");
        root.Remove("metricViewTotal");
        root.Remove("metricViewReturned");
        root["metricViewsRequested"] = false;
        using var withoutMetrics = JsonDocument.Parse(root.ToJsonString());
        var actual = BridgeProjection.ProjectRangeShaders(withoutMetrics.RootElement, null, "0.2.12.71.0", null, null, 0, int.MaxValue);
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));

        root["selectionMatchesTarget"] = false;
        using var wrongScope = JsonDocument.Parse(root.ToJsonString());
        Assert.Throws<BridgeSchemaException>(() => BridgeProjection.ProjectRangeShaders(
            wrongScope.RootElement, null, "0.2.12.71.0", null, null, 0, int.MaxValue));

        root["selectionMatchesTarget"] = true;
        root["metricsStable"] = false;
        using var unstable = JsonDocument.Parse(root.ToJsonString());
        Assert.Throws<BridgeSchemaException>(() => BridgeProjection.ProjectRangeShaders(
            unstable.RootElement, null, "0.2.12.71.0", null, null, 0, int.MaxValue));
    }
}
