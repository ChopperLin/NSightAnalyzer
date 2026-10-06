using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NsightAnalyzer.Adapters.NsightViewer2026_2;
using NsightAnalyzer.Cli;
using NsightAnalyzer.Contracts;
using NsightAnalyzer.Wrappers;
using Xunit;

namespace NsightAnalyzer.Tests;

public sealed class AgentEfficiencyTests
{
    private static string Render(string operation, OperationResult result, bool detail = false) =>
        JsonRenderer.Render(new(operation, SchemaVersion.V2,
            AgentResponseProjection.Apply(result, detail))).Json;

    [Fact]
    public void DiscoveryIsSmallAndOperationHelpIsComplete()
    {
        var catalog = Render("capabilities", OperationRegistry.Describe());
        Assert.True(Encoding.UTF8.GetByteCount(catalog) < 8_000);
        Assert.DoesNotContain('\n', catalog);
        using var json = JsonDocument.Parse(catalog);
        var items = json.RootElement.GetProperty("result").GetProperty("value")
            .GetProperty("operations").GetProperty("items");
        foreach (var item in items.EnumerateArray())
        {
            Assert.False(item.TryGetProperty("parameters", out _));
            var help = OperationRegistry.DescribeOperation(item.GetProperty("id").GetString()!);
            Assert.NotEmpty(Assert.IsType<OperationDescriptor>(help.Value).Parameters!);
        }
    }

    [Fact]
    public void BareInvocationAndScopedHelpNeedNoFailedDiscoveryCall()
    {
        Assert.Equal("capabilities", CommandLine.Parse(EntryPoint.NormalizeHelp([])).Command!.Operation);
        var help = CommandLine.Parse(EntryPoint.NormalizeHelp(["inspect-pass", "--help"]));
        Assert.Equal("describe", help.Command!.Operation);
        Assert.Equal("inspect-pass", help.Command.DescribedOperation);
        Assert.False(CommandLine.Parse(EntryPoint.NormalizeHelp(["--bogus"])).IsSuccess);
    }

    [Fact]
    public void DefaultsBoundOutputAndAvoidUnrequestedAnalysis()
    {
        var inspection = CommandLine.Parse(["inspect-pass", "fixture.ngfx-gputrace", "--event-ordinal", "1773"]).Command!;
        Assert.True(inspection.Compact);
        Assert.False(inspection.Detail);
        Assert.Equal(20, inspection.Limit);
        Assert.Equal(5, inspection.TopShaderCount);
        Assert.Equal(RangeSections.Metrics | RangeSections.Shaders, inspection.Sections);
        var comparison = CommandLine.Parse(["compare-ranges", "fixture.ngfx-gputrace",
            "--event-ordinal", "1773", "--baseline-event-ordinal", "3901"]).Command!;
        Assert.Equal(RangeSections.Metrics, comparison.Sections);
        Assert.Equal("marker", CommandLine.Parse(["find-ranges", "fixture.ngfx-gputrace"]).Command!.Grain);
    }

    [Theory]
    [InlineData("metrics,unknown")]
    [InlineData("shaders,shaders")]
    [InlineData("")]
    public void InvalidSectionSelectionsAreRejectedBeforeOpeningViewer(string sections)
    {
        Assert.False(CommandLine.Parse(["inspect-pass", "fixture.ngfx-gputrace",
            "--event-ordinal", "1773", "--sections", sections]).IsSuccess);
    }

    [Fact]
    public void BriefInspectionRetainsExactMetricsCoverageAndProvenance()
    {
        var data = Inspection();
        var projected = InspectPassWrapper.Project(data, 5);
        Assert.Equal(801, projected.Metrics!.Values.TotalCount);
        Assert.Equal(20, projected.Metrics.Values.ReturnedCount);
        Assert.Equal(20, projected.Metrics.Values.NextCursor);
        Assert.True(projected.Metrics.Values.Truncated);
        Assert.Equal(data.Shaders.Sum(item => item.SampleCount), projected.Shaders!.TotalSampleCount);
        Assert.Equal(5, projected.Shaders.ReturnedCount);
        var result = OperationResult.Success(projected, [Provenance]);
        var brief = Render("inspect-pass", result);
        var detail = Render("inspect-pass", result, true);
        Assert.True(Encoding.UTF8.GetByteCount(brief) < 20_000);
        using var briefJson = JsonDocument.Parse(brief);
        using var detailJson = JsonDocument.Parse(detail);
        var briefResult = briefJson.RootElement.GetProperty("result");
        var detailResult = detailJson.RootElement.GetProperty("result");
        Assert.Equal(detailResult.GetProperty("provenance").GetRawText(), briefResult.GetProperty("provenance").GetRawText());
        var briefValue = briefResult.GetProperty("value");
        var detailValue = detailResult.GetProperty("value");
        Assert.False(briefValue.TryGetProperty("instructionMix", out _));
        Assert.Equal(detailValue.GetProperty("shaders").GetProperty("sampleCoveragePercent").GetRawText(),
            briefValue.GetProperty("shaders").GetProperty("sampleCoveragePercent").GetRawText());
        var briefItems = briefValue.GetProperty("metrics").GetProperty("values").GetProperty("items");
        var detailItems = detailValue.GetProperty("metrics").GetProperty("values").GetProperty("items");
        for (var index = 0; index < briefItems.GetArrayLength(); index++)
        {
            foreach (var property in briefItems[index].EnumerateObject())
            {
                Assert.Equal(detailItems[index].GetProperty(property.Name).GetRawText(), property.Value.GetRawText());
            }
            Assert.False(briefItems[index].TryGetProperty("description", out _));
        }
        Assert.False(briefValue.GetProperty("shaders").GetProperty("items")[0].TryGetProperty("instructionMix", out _));
        Assert.True(detailValue.GetProperty("shaders").GetProperty("items")[0].TryGetProperty("instructionMix", out _));
    }

    [Fact]
    public void InspectionPagesCloseWithoutLosingOccurrences()
    {
        var data = Inspection();
        var actual = new List<RangeMetricFact>();
        var cursor = 0;
        do
        {
            var page = InspectPassWrapper.Project(data, 5, cursor, 20).Metrics!.Values;
            Assert.Null(WrapperSupport.ValidatePage(page, cursor, 20, 801));
            actual.AddRange(page.Items);
            if (page.NextCursor is null) break;
            cursor = page.NextCursor.Value;
        } while (true);
        Assert.Equal(data.Metrics, actual);
    }

    [Fact]
    public void MetricsComparisonDoesNotRequireShaderOrInstructionData()
    {
        var data = Inspection() with { Sections = RangeSections.Metrics, Shaders = [] };
        var baseline = data with { Metrics = data.Metrics.Skip(1).ToArray() };
        var result = CompareRangesWrapper.Project(data, baseline, "target.ngfx-gputrace", "base.ngfx-gputrace",
            5, 0, 500, RangeSections.Metrics, new WrapperDeadline(1000));
        Assert.True(result.IsSuccess);
        var comparison = Assert.IsType<CompareRangesValue>(result.Value);
        Assert.Null(comparison.Shaders);
        Assert.Null(comparison.InstructionMix);
        Assert.Null(comparison.Stalls);
        Assert.Equal(1, comparison.Metrics!.TargetOnlyCount);
        Assert.Equal("changed", comparison.Metrics.DeltaFilter);
        Assert.Equal(1, comparison.Metrics.Deltas.TotalCount);
        var missing = Assert.Single(comparison.Metrics.Deltas.Items, item => item.JoinState == "targetOnly");
        Assert.Null(missing.Baseline);
        Assert.Null(missing.Delta);
        var complete = CompareRangesWrapper.Project(data, data, "target.ngfx-gputrace", "base.ngfx-gputrace",
            5, 0, 20, RangeSections.Metrics, new WrapperDeadline(1000), includeUnchanged: true);
        Assert.Equal(801, Assert.IsType<CompareRangesValue>(complete.Value).Metrics!.Deltas.TotalCount);
        var unchanged = CompareRangesWrapper.Project(data, data, "target.ngfx-gputrace", "base.ngfx-gputrace",
            5, 0, 20, RangeSections.Metrics, new WrapperDeadline(1000));
        var noChanges = Assert.IsType<CompareRangesValue>(unchanged.Value).Metrics!;
        Assert.Equal(801, noChanges.MatchedCount);
        Assert.Empty(noChanges.Deltas.Items);
        Assert.Equal(0, noChanges.Deltas.TotalCount);
    }

    [Fact]
    public void ResponseOverflowSuggestsSupportedPagingAndDoesNotReturnPartialSuccess()
    {
        var rendered = JsonRenderer.Render(new("inspect-pass", SchemaVersion.V2,
            OperationResult.Success(new { value = new string('x', ContractLimits.MaximumResponseBytes) })));
        Assert.False(rendered.WithinBound);
        using var document = JsonDocument.Parse(rendered.Json);
        var result = document.RootElement.GetProperty("result");
        Assert.False(result.GetProperty("isSuccess").GetBoolean());
        var recovery = result.GetProperty("error").GetProperty("recovery");
        Assert.Equal("reducePageOrShaderCount", recovery.GetProperty("action").GetString());
        Assert.False(recovery.TryGetProperty("parameter", out _));
        Assert.True(CommandLine.Parse(["inspect-pass", "fixture.ngfx-gputrace", "--event-ordinal", "1773", "--limit", "1"]).IsSuccess);
        Assert.True(CommandLine.Parse(["inspect-pass", "fixture.ngfx-gputrace", "--event-ordinal", "1773",
            "--sections", "shaders", "--top-shaders", "1"]).IsSuccess);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ShaderPagesAreShortenedToTheActualSerializedByteBudget(bool compact)
    {
        var inspection = Inspection();
        var requested = Enumerable.Range(0, ContractLimits.MaximumShaderPageLimit)
            .Select(index => inspection.Shaders[index % inspection.Shaders.Count] with
            {
                Key = inspection.Shaders[index % inspection.Shaders.Count].Key with
                {
                    PreorderOrdinal = index,
                    TreePath = [index],
                    Name = new string('s', 8_192),
                },
            })
            .ToArray();
        var value = new RangeShadersValue(
            inspection.Event.Key,
            new(requested, 0, ContractLimits.MaximumShaderPageLimit, 600,
                requested.Length, true, requested.Length));
        var original = OperationResult.Success(value, [Provenance]);

        Assert.False(JsonRenderer.Render(
            new("trace.range-shaders", SchemaVersion.V2, original), compact).WithinBound);

        var fitted = ResponseBudget.Fit(
            "trace.range-shaders", original, detail: true, compact);
        var page = Assert.IsType<RangeShadersValue>(fitted.Value).Shaders;
        Assert.InRange(page.ReturnedCount, 1, requested.Length - 1);
        Assert.Equal(ContractLimits.MaximumShaderPageLimit, page.Limit);
        Assert.Equal(page.ReturnedCount, page.NextCursor);
        Assert.True(page.Truncated);
        Assert.Equal(requested.Take(page.ReturnedCount), page.Items);
        var warning = Assert.Single(fitted.Warnings!, warning =>
            warning.Code == ResponseBudget.PageReducedWarningCode);
        Assert.Contains($"--cursor {page.NextCursor}", warning.Message);
        Assert.True(JsonRenderer.Render(new(
            "trace.range-shaders",
            SchemaVersion.V2,
            AgentResponseProjection.Apply(fitted, detail: true)), compact).WithinBound);
    }

    private static PassInspectionData Inspection()
    {
        using var metrics = FixtureBridge.Load("range-metrics-gbufferpass.json");
        using var shaders = FixtureBridge.Load("range-shaders-gbufferpass.json");
        var m = BridgeProjection.ProjectRangeMetrics(metrics.RootElement, 1773, null, [], 0, int.MaxValue);
        var s = BridgeProjection.ProjectRangeShaders(shaders.RootElement, null, "0.2.12.71.0", null, null, 0, int.MaxValue);
        return new(new(m.Scope, 4, 1, null, null, null, null, null, null, null),
            m.TableCount, m.RowCount, m.Metrics.Items, s.Shaders.Items, null, null,
            new WrapperContext(new WrapperDeadline(1000)), RangeSections.Metrics | RangeSections.Shaders);
    }

    private static readonly FactProvenance Provenance = new("fixture", "unsupportedVersionPinnedViewer",
        "2026.2.0.0", "37991608", "public-release", "6.8.1", "probe-0.51", "fixture-snapshot", "fixture/v1", "inspection/v2", "fixture");
}

public sealed class ShaderSourceIdentityTests
{
    [Fact]
    public void IndistinguishableSourceDuplicatesAreUnavailableInsteadOfGuessed()
    {
        using var inventory = FixtureBridge.Load("range-shaders-gbufferpass.json");
        var key = PixelKey(inventory);
        var selected = BridgeProjection.ResolveShaderSourceSelection(inventory.RootElement,
            null, "0.2.12.71.0", key.Stage, key.Hash!, key.HashOccurrence);
        var root = JsonNode.Parse(inventory.RootElement.GetRawText())!;
        var export = root["models"]!.AsArray().Single(model =>
            model!["class"]!.GetValue<string>().EndsWith("SampleItemTreeModel", StringComparison.Ordinal))!["export"]!;
        var nodes = export["nodes"]!.AsArray();
        bool At(JsonNode node, IEnumerable<int> path) =>
            node["path"]!.AsArray().Select(value => value!.GetValue<int>()).SequenceEqual(path);
        var duplicate = nodes.Single(node => At(node!, selected.Shader.TreePath))!.DeepClone();
        var parent = nodes.Single(node => At(node!, selected.Shader.TreePath.Take(1)))!.DeepClone();
        var ordinal = nodes.Max(node => node!["ordinal"]!.GetValue<int>()) + 1;
        parent["path"] = JsonSerializer.SerializeToNode(new[] { 999 });
        parent["ordinal"] = ordinal;
        parent["childCount"] = 1;
        duplicate["path"] = JsonSerializer.SerializeToNode(new[] { 999, 0 });
        duplicate["ordinal"] = ordinal + 1;
        nodes.Add(parent);
        nodes.Add(duplicate);
        export["totalCount"] = nodes.Count;
        export["returnedCount"] = nodes.Count;
        using var duplicateInventory = JsonDocument.Parse(root.ToJsonString());
        var error = Assert.Throws<BridgeFactUnavailableException>(() =>
            BridgeProjection.ResolveShaderSourceSelection(duplicateInventory.RootElement,
                null, "0.2.12.71.0", key.Stage, key.Hash!, key.HashOccurrence));
        Assert.Equal("trace.shader_source_identity_ambiguous", error.Code);
    }

    [Fact]
    public void ReturnedStageHashOccurrenceRoundTripsToSource()
    {
        using var inventory = FixtureBridge.Load("range-shaders-gbufferpass.json");
        var key = PixelKey(inventory);
        var selection = BridgeProjection.ResolveShaderSourceSelection(inventory.RootElement,
            null, "0.2.12.71.0", key.Stage, key.Hash!, key.HashOccurrence);
        using var source = Source(inventory, selection, false);
        var value = BridgeProjection.ProjectShaderSource(source.RootElement, null, "0.2.12.71.0", selection, 1, 20);
        Assert.Equal(selection.Shader.Stage, value.Shader.Stage);
        Assert.Equal(selection.Shader.HashOccurrence, value.Shader.HashOccurrence);
        Assert.Equal(selection.Shader.Pipeline, value.Shader.Pipeline);
        Assert.Equal(selection.Shader.TreePath, value.Shader.TreePath);
    }

    [Fact]
    public void MatchingHashAndOccurrenceCannotHideTheWrongStage()
    {
        using var inventory = FixtureBridge.Load("range-shaders-gbufferpass.json");
        var key = PixelKey(inventory);
        var selection = BridgeProjection.ResolveShaderSourceSelection(inventory.RootElement,
            null, "0.2.12.71.0", key.Stage, key.Hash!, key.HashOccurrence);
        using var source = Source(inventory, selection, true);
        Assert.Throws<BridgeSchemaException>(() => BridgeProjection.ProjectShaderSource(
            source.RootElement, null, "0.2.12.71.0", selection, 1, 20));
    }

    private static JsonDocument Source(JsonDocument inventory, ShaderSourceSelection selection, bool wrongStage)
    {
        var root = JsonNode.Parse(inventory.RootElement.GetRawText())!.AsObject();
        var key = selection.Shader;
        root["modelSelectionMatchesTarget"] = true;
        root["comboSelectionMatchesTarget"] = true;
        root["modelSelectionTargetProviderReady"] = true;
        root["targetModelSelection"] = JsonSerializer.SerializeToNode(new
        {
            // A sorted proxy legitimately has another row path.
            path = new[] { 1, 0 },
            cells = new[] { wrongStage ? "Compute" : key.Stage, "", key.Name, key.Hash },
        });
        root["targetModelSelectionParent"] = JsonSerializer.SerializeToNode(new { cells = new[] { "", "", key.Pipeline } });
        root["modelSelector"] = JsonSerializer.SerializeToNode(new { kind = "match", column = 3, occurrence = selection.HashMatchOccurrence, value = key.Hash });
        root["comboSelectionDerivedShaderName"] = key.Name;
        root["comboSelectionDerivedPipelineName"] = key.Pipeline;
        root["currentComboSelection"] = JsonSerializer.SerializeToNode(new { text = $"{key.Pipeline} - {key.Name}" });
        root["models"] = JsonNode.Parse("""
            [{"class":"NV::SourceCorrelation::SourceModel","parentId":"fixture","export":{
            "totalCountExact":true,"truncated":false,"returnedCount":1,"totalCount":1,"headers":[],
            "nodes":[{"ordinal":0,"cells":[{"column":3,"display":"placeholder"}]}]}}]
            """);
        root["models"]![0]!["export"]!["nodes"]![0]!["cells"]![0]!["display"] = $"dxil ({key.Hash![2..]})";
        return JsonDocument.Parse(root.ToJsonString());
    }

    private static ShaderKey PixelKey(JsonDocument inventory) =>
        BridgeProjection.ProjectRangeShaders(inventory.RootElement,
            null, "0.2.12.71.0", null, null, 0, int.MaxValue).Shaders.Items
            .First(shader => shader.Key.Stage == "Pixel").Key;
}
