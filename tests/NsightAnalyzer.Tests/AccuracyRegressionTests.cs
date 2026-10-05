using System.Text.Json;
using System.Text.Json.Nodes;
using NsightAnalyzer.Adapters.NsightViewer2026_2;
using NsightAnalyzer.Contracts;
using NsightAnalyzer.Wrappers;
using Xunit;

namespace NsightAnalyzer.Tests;

public sealed class AccuracyRegressionTests
{
    [Theory]
    [InlineData(null, "trace.shader_samples_absent")]
    [InlineData("null", "trace.shader_samples_absent")]
    [InlineData("\"N/A\"", "trace.shader_samples_unavailable")]
    public void MissingShaderSamplesNeverBecomeZero(string? replacement, string code)
    {
        using var fixture = MutatedSamples(replacement);
        var error = Assert.Throws<BridgeFactUnavailableException>(() => Shaders(fixture));
        Assert.Equal(code, error.Code);
    }

    [Theory]
    [InlineData("[1,\"invalid\"]")]
    [InlineData("[1,-1]")]
    [InlineData("[9223372036854775807,1]")]
    public void InvalidSampleVectorsCannotProducePartialSums(string replacement)
    {
        using var fixture = MutatedSamples(replacement);
        Assert.Throws<BridgeSchemaException>(() => Shaders(fixture));
    }

    [Theory]
    [InlineData("[]", "empty")]
    [InlineData("[0]", "available")]
    public void ExplicitEmptyAndMeasuredZeroSamplesKeepDifferentStates(string replacement, string availability)
    {
        using var original = FixtureBridge.Load("range-shaders-gbufferpass.json");
        var key = Shaders(original).Shaders.Items.MaxBy(shader => shader.SampleCount)!.Key;
        using var fixture = MutatedSamples(replacement);
        var shader = Shaders(fixture).Shaders.Items.Single(item => item.Key.PreorderOrdinal == key.PreorderOrdinal);
        Assert.Equal(0, shader.SampleCount);
        Assert.Equal(availability, shader.SampleAvailability);
    }

    [Fact]
    public void MissingDependencySamplesAreNotAnEmptyAttributionVector()
    {
        using var fixture = MutatedSamples(null, 13);
        Assert.Throws<BridgeFactUnavailableException>(() => Shaders(fixture));
        using var original = FixtureBridge.Load("range-shaders-gbufferpass.json");
        Assert.Contains(Shaders(original).Shaders.Items,
            item => item.DependencyAttributedSampleCount == 0 && item.DependencySampleAvailability == "empty");
    }

    [Fact]
    public void AnUnattributedLeafRetainsItsSampleCountWithoutInventingAHash()
    {
        using var original = FixtureBridge.Load("range-shaders-gbufferpass.json");
        var selected = Shaders(original).Shaders.Items.MaxBy(shader => shader.SampleCount)!;
        var root = JsonNode.Parse(original.RootElement.GetRawText())!;
        var node = root["models"]![0]!["export"]!["nodes"]!.AsArray()
            .Single(item => item!["ordinal"]!.GetValue<int>() == selected.Key.PreorderOrdinal)!;
        var cells = node["cells"]!.AsArray();
        cells.Single(item => item!["column"]!.GetValue<int>() == 0)!["display"] = "Internal";
        cells.Single(item => item!["column"]!.GetValue<int>() == 3)!["display"] = "--";
        using var fixture = JsonDocument.Parse(root.ToJsonString());
        var projected = Shaders(fixture).Shaders.Items.Single(item => item.Key.PreorderOrdinal == selected.Key.PreorderOrdinal);
        Assert.Null(projected.Key.Hash);
        Assert.Equal(selected.SampleCount, projected.SampleCount);
        Assert.Equal("available", projected.SampleAvailability);
    }

    [Fact]
    public void SourceSelectionSeparatesStageOccurrenceFromHashMatchOccurrence()
    {
        using var original = FixtureBridge.Load("range-shaders-gbufferpass.json");
        var facts = Shaders(original).Shaders.Items;
        var pixel = facts.First(shader => shader.Key.Stage == "Pixel").Key;
        var compute = facts.First(shader => shader.Key.Stage == "Compute").Key;
        var root = JsonNode.Parse(original.RootElement.GetRawText())!;
        var node = root["models"]![0]!["export"]!["nodes"]!.AsArray()
            .Single(item => item!["ordinal"]!.GetValue<int>() == compute.PreorderOrdinal)!;
        node["cells"]!.AsArray().Single(item => item!["column"]!.GetValue<int>() == 3)!["display"] = pixel.Hash;
        using var fixture = JsonDocument.Parse(root.ToJsonString());
        var selection = BridgeProjection.ResolveShaderSourceSelection(fixture.RootElement,
            null, "0.2.12.71.0", pixel.Stage, pixel.Hash!, pixel.HashOccurrence);
        Assert.Equal(pixel.Stage, selection.Shader.Stage);
        Assert.Equal(pixel.HashOccurrence, selection.Shader.HashOccurrence);
        Assert.True(selection.HashMatchOccurrence > selection.Shader.HashOccurrence);
    }

    [Fact]
    public void APartialMetricTableMatchIsAnExplicitFailure()
    {
        using var fixture = FixtureBridge.Load("range-metrics-gbufferpass.json");
        var error = Assert.Throws<BridgeFactNotFoundException>(() =>
            BridgeProjection.ProjectRangeMetrics(fixture.RootElement, 1773, null,
                ["SM Register Occupancy", "Missing Table"], 0, 20));
        Assert.Equal("trace.metric_table_not_found", error.Code);
        Assert.Contains("Missing Table", error.Message);
        var available = BridgeProjection.ProjectRangeMetrics(fixture.RootElement, 1773, null,
            ["SM Register Occupancy", "sm register occupancy"], 0, 20);
        Assert.Equal(1, available.TableCount);
    }

    [Fact]
    public void DisplayFallbackReportsItsActualSourceAndResolution()
    {
        var data = Inspection();
        var metric = data.Metrics[0];
        var result = Comparison(data with { Metrics = [metric with { DisplayValue = "1.0", PreciseValue = "invalid" }] },
            data with { Metrics = [metric with { DisplayValue = "0.0", PreciseValue = "0.000000" }] }, RangeSections.Metrics);
        var delta = Assert.Single(result.Metrics!.Deltas.Items);
        Assert.Equal("displayValue", delta.Target!.NumericSource);
        Assert.Equal("displayValue", delta.Target.ValueSource);
        Assert.Equal("1.0", delta.Target.Value);
        Assert.Equal("parsedDisplay", delta.Target.NumericState);
        Assert.Equal(0.1m, delta.Target.NumericResolution);
        Assert.Equal(1m, delta.Delta);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("1.0")]
    [InlineData("invalid")]
    public void EqualNumericValuesDoNotHideChangedEvidence(string? targetPrecise)
    {
        var data = Inspection();
        var metric = data.Metrics[0];
        var target = metric with { DisplayValue = "1.0", PreciseValue = targetPrecise };
        var baseline = metric with { DisplayValue = "1.0", PreciseValue = "1.000000" };
        var result = Comparison(data with { Metrics = [target] }, data with { Metrics = [baseline] }, RangeSections.Metrics);
        var delta = Assert.Single(result.Metrics!.Deltas.Items);
        Assert.True(delta.Changed);
        Assert.Equal(0m, delta.Delta);
        Assert.Equal(1, result.Metrics.ChangedCount);
    }

    [Fact]
    public void BoundedPreciseEvidenceCannotBecomeAnExactDisplayDelta()
    {
        var data = Inspection();
        var metric = data.Metrics[0];
        var result = Comparison(data with { Metrics = [metric with { DisplayValue = "1.0", PreciseValue = "<1.0" }] },
            data with { Metrics = [metric with { DisplayValue = "1.0", PreciseValue = "1.000000" }] }, RangeSections.Metrics);
        var delta = Assert.Single(result.Metrics!.Deltas.Items);
        Assert.Equal("bounded", delta.Target!.NumericState);
        Assert.Null(delta.Target.NumericValue);
        Assert.Null(delta.Delta);
    }

    [Fact]
    public void RestoredFixturePreservesDistinctAndSharedShaderHashes()
    {
        using var fixture = FixtureBridge.Load("range-shaders-gbufferpass.json");
        var facts = Shaders(fixture).Shaders.Items;
        Assert.Equal(115, facts.Count);
        Assert.Equal(110, facts.Select(item => item.Key.Hash).Distinct().Count());
        Assert.DoesNotContain(facts, item => item.Key.Hash == "0x0000000000000000");
    }

    [Fact]
    public void ASharedShaderStillJoinsWhenAnEarlierPipelineIsAbsent()
    {
        var data = Inspection();
        var shared = data.Shaders.GroupBy(shader => (shader.Key.Stage, shader.Key.Hash))
            .First(group => group.Count() > 1 && group.Select(shader => shader.Key.Pipeline).Distinct().Count() == group.Count())
            .OrderBy(shader => shader.Key.HashOccurrence).ToArray();
        var survivor = shared[^1] with { Key = shared[^1].Key with { HashOccurrence = 0 } };
        var result = Comparison(data with { Shaders = [survivor] }, data with { Shaders = shared }, RangeSections.Shaders);
        Assert.Equal(1, result.Shaders!.MatchedCount);
        Assert.Equal(0, result.Shaders.TargetOnlyCount);
        Assert.Equal(shared.Length - 1, result.Shaders.BaselineOnlyCount);
        var matched = Assert.Single(result.Shaders.Items, item => item.JoinState == "matched");
        Assert.Equal(0, matched.SampleCountDelta);
        Assert.False(matched.Changed);
        Assert.NotEqual(matched.Target!.Key.HashOccurrence, matched.Baseline!.Key.HashOccurrence);
    }

    [Fact]
    public void IndistinguishableCrossRangeShadersAreNotPairedByOccurrence()
    {
        var data = Inspection();
        var shader = data.Shaders[0];
        var duplicate = shader with { Key = shader.Key with { HashOccurrence = shader.Key.HashOccurrence + 1 } };
        var result = CompareRangesWrapper.Project(data with { Shaders = [shader, duplicate] }, data,
            "target.ngfx-gputrace", "baseline.ngfx-gputrace", 5, 0, 20, RangeSections.Shaders, new WrapperDeadline(1000));
        Assert.False(result.IsSuccess);
        Assert.Equal("wrapper.shader_comparison_ambiguous", result.Error!.Code);
    }

    [Fact]
    public void TimingOnlyNeedsNoMetricShaderOrInstructionFacts()
    {
        Assert.True(RangeSectionNames.TryParse("timing", out var sections));
        var data = Inspection() with { Metrics = [], Shaders = [], Sections = sections };
        var baseline = data with { Event = data.Event with { GpuDuration = "1 ms", Duration = "1 ms" } };
        var target = data with { Event = data.Event with { GpuDuration = "2 ms", Duration = "2 ms" } };
        var inspection = InspectPassWrapper.Project(target, 5);
        Assert.Equal(["timing"], inspection.Sections);
        Assert.Null(inspection.Metrics);
        Assert.Null(inspection.Shaders);
        Assert.Null(inspection.InstructionMix);
        var comparison = Comparison(target, baseline, sections);
        Assert.Null(comparison.Metrics);
        Assert.Null(comparison.Shaders);
        Assert.Null(comparison.InstructionMix);
        Assert.Equal(1m, comparison.Durations.Single(item => item.Field == "gpuDuration").DeltaMilliseconds);
    }

    private static JsonDocument MutatedSamples(string? replacement, int column = 6)
    {
        using var original = FixtureBridge.Load("range-shaders-gbufferpass.json");
        var selected = Shaders(original).Shaders.Items.MaxBy(shader => shader.SampleCount)!;
        var root = JsonNode.Parse(original.RootElement.GetRawText())!;
        var model = root["models"]!.AsArray().Single(item => item!["class"]!.GetValue<string>().EndsWith("SampleItemTreeModel"));
        var node = model!["export"]!["nodes"]!.AsArray().Single(item => item!["ordinal"]!.GetValue<int>() == selected.Key.PreorderOrdinal);
        var cell = node!["cells"]!.AsArray().Single(item => item!["column"]!.GetValue<int>() == column)!.AsObject();
        if (replacement is null) cell.Remove("display");
        else cell["display"] = JsonNode.Parse(replacement);
        return JsonDocument.Parse(root.ToJsonString());
    }

    private static RangeShadersValue Shaders(JsonDocument document) =>
        BridgeProjection.ProjectRangeShaders(document.RootElement, null, "0.2.12.71.0", null, null, 0, int.MaxValue);

    private static PassInspectionData Inspection()
    {
        using var metricFixture = FixtureBridge.Load("range-metrics-gbufferpass.json");
        using var shaderFixture = FixtureBridge.Load("range-shaders-gbufferpass.json");
        var metrics = BridgeProjection.ProjectRangeMetrics(metricFixture.RootElement, 1773, null, [], 0, int.MaxValue);
        return new(new(metrics.Scope, 4, 1, null, null, null, null, null, null, null),
            metrics.TableCount, metrics.RowCount, metrics.Metrics.Items, Shaders(shaderFixture).Shaders.Items,
            null, null, new WrapperContext(new WrapperDeadline(1000)), RangeSections.Metrics | RangeSections.Shaders);
    }

    private static CompareRangesValue Comparison(PassInspectionData target, PassInspectionData baseline, RangeSections sections)
    {
        var result = CompareRangesWrapper.Project(target, baseline, "target.ngfx-gputrace", "baseline.ngfx-gputrace",
            150, 0, 500, sections, new WrapperDeadline(1000));
        Assert.True(result.IsSuccess, result.Error?.Message);
        return Assert.IsType<CompareRangesValue>(result.Value);
    }
}
