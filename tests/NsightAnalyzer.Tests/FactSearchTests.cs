using NsightAnalyzer.Adapters.NsightViewer2026_2;
using NsightAnalyzer.Contracts;
using NsightAnalyzer.Wrappers;
using Xunit;

namespace NsightAnalyzer.Tests;

public sealed class FindMetricsTests
{
    [Fact]
    public void NameSearchPagesStableFullMetricIdentities()
    {
        var source = Metrics();
        var expected = source.Metrics.Items.Where(item =>
                item.TableName.Contains("register", StringComparison.OrdinalIgnoreCase) ||
                item.RowName.Contains("register", StringComparison.OrdinalIgnoreCase) ||
                item.ColumnName.Contains("register", StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.SourceOrdinal).ToArray();
        Assert.NotEmpty(expected);
        var actual = new List<RangeMetricFact>();
        var cursor = 0;
        do
        {
            var result = FindMetricsWrapper.Project(source, [], "REGISTER", null, cursor, 3, Stats);
            Assert.True(result.IsSuccess);
            var page = result.Value!.Metrics;
            Assert.Equal(source.Scope, result.Value.Scope);
            Assert.Equal(801, result.Value.ScannedMetricCount);
            Assert.Null(WrapperSupport.ValidatePage(page, cursor, 3, expected.Length));
            actual.AddRange(page.Items);
            if (page.NextCursor is null) break;
            cursor = page.NextCursor.Value;
        } while (true);
        Assert.Equal(expected, actual);
        Assert.Equal(actual.Count, actual.Select(item => item.SourceOrdinal).Distinct().Count());
    }

    [Fact]
    public void ReturnedOrdinalResolvesOneExactMetricWithoutRenumberingTableFilters()
    {
        var complete = Metrics();
        var selected = complete.Metrics.Items.First(item => item.TableName == "SM Register Occupancy");
        Assert.True(selected.SourceOrdinal > 0);
        using var fixture = FixtureBridge.Load("range-metrics-gbufferpass.json");
        var table = BridgeProjection.ProjectRangeMetrics(fixture.RootElement,
            1773, null, [selected.TableName], 0, int.MaxValue);
        var result = FindMetricsWrapper.Project(table, [selected.TableName], null,
            selected.SourceOrdinal, 0, 20, Stats);
        Assert.True(result.IsSuccess);
        var actual = Assert.Single(result.Value!.Metrics.Items);
        Assert.Equal(selected, actual);
        Assert.Equal(selected.PreciseValue, actual.PreciseValue);
        Assert.Equal(selected.TableOccurrence, actual.TableOccurrence);
        Assert.Equal(selected.RowOccurrence, actual.RowOccurrence);
        Assert.Equal(selected.ColumnOccurrence, actual.ColumnOccurrence);
    }

    [Fact]
    public void NoNameMatchesIsCompleteEmptyButMissingExactOrdinalIsNotFound()
    {
        var source = Metrics();
        var empty = FindMetricsWrapper.Project(source, [], "not-an-existing-metric", null, 0, 20, Stats);
        Assert.True(empty.IsSuccess);
        Assert.Empty(empty.Value!.Metrics.Items);
        Assert.Equal(0, empty.Value.Metrics.TotalCount);
        Assert.False(empty.Value.Metrics.Truncated);
        var missing = FindMetricsWrapper.Project(source, [], null, int.MaxValue, 0, 20, Stats);
        Assert.False(missing.IsSuccess);
        Assert.Equal("wrapper.metric_not_found", missing.Error!.Code);
    }

    [Fact]
    public void SearchRefusesAnIncompleteSourceOrAmbiguousSourceOrdinals()
    {
        var source = Metrics();
        var partial = source with { Metrics = WrapperSupport.Page(source.Metrics.Items, 0, 20) };
        Assert.False(FindMetricsWrapper.Project(partial, [], "register", null, 0, 20, Stats).IsSuccess);
        var duplicate = source.Metrics.Items.Concat([source.Metrics.Items[0]]).ToArray();
        source = source with { Metrics = WrapperSupport.Page(duplicate, 0, int.MaxValue) };
        Assert.False(FindMetricsWrapper.Project(source, [], "register", null, 0, 20, Stats).IsSuccess);
    }

    private static RangeMetricsValue Metrics()
    {
        using var fixture = FixtureBridge.Load("range-metrics-gbufferpass.json");
        return BridgeProjection.ProjectRangeMetrics(fixture.RootElement, 1773, null, [], 0, int.MaxValue);
    }

    private static readonly WrapperExecutionStats Stats = new(1, 801, 0, 0);
}

public sealed class SourceHotspotsTests
{
    [Fact]
    public void RealSourceFixtureReturnsUsefulRowsInsteadOfTheEmptyLeadingPage()
    {
        var source = Source();
        Assert.Equal(965, source.Rows.TotalCount);
        Assert.Equal(0, source.Rows.Items.Take(20).Sum(row => row.SampleCount));
        var result = SourceHotspotsWrapper.Project(source, "dxil", 0, 0, 20, Stats);
        Assert.True(result.IsSuccess);
        var value = result.Value!;
        Assert.Equal(480, value.View.RowCount);
        Assert.Equal(3592, value.View.AttributedSampleCount);
        Assert.Equal(170, value.Rows.TotalCount);
        Assert.Equal(2416, value.ReturnedSampleCount);
        Assert.Equal(67.260579m, value.SampleCoveragePercent);
        Assert.Equal(new[] { 252, 211, 367, 171, 164 },
            value.Rows.Items.Take(5).Select(row => row.SourceOrdinal));
        Assert.All(value.Rows.Items, row =>
        {
            Assert.Equal("dxil", row.ViewKind);
            Assert.True(row.SampleCount > 0);
            Assert.Equal(source.Rows.Items.Single(original =>
                original.SourceOrdinal == row.SourceOrdinal), row);
        });
        Assert.Equal(source.Scope, value.Scope);
        Assert.Equal(source.Shader, value.Shader);
    }

    [Fact]
    public void PagesCloseOnceToOneViewAndNeverDoubleCountDxilAndSass()
    {
        var source = Source();
        var actual = new List<ShaderSourceRowFact>();
        var cursor = 0;
        do
        {
            var result = SourceHotspotsWrapper.Project(source, "dxil", 0, cursor, 20, Stats);
            Assert.True(result.IsSuccess);
            var value = result.Value!;
            var page = value.Rows;
            Assert.Null(WrapperSupport.ValidatePage(page, cursor, 20, 170));
            Assert.Equal(page.Items.Sum(row => row.SampleCount), value.ReturnedSampleCount);
            actual.AddRange(page.Items);
            if (page.NextCursor is null) break;
            cursor = page.NextCursor.Value;
        } while (true);
        Assert.Equal(170, actual.Count);
        Assert.Equal(170, actual.Select(row => row.SourceOrdinal).Distinct().Count());
        Assert.Equal(3592, actual.Sum(row => row.SampleCount));
        Assert.Equal(actual.OrderByDescending(row => row.SampleCount).ThenBy(row => row.SourceOrdinal), actual);

        var sass = SourceHotspotsWrapper.Project(source, "sass", 0, 0, 20, Stats).Value!;
        Assert.Equal(3592, sass.View.AttributedSampleCount);
        Assert.Equal(2282, sass.ReturnedSampleCount);
        Assert.Equal("unavailableInViewerSku", sass.View.OpcodeTextAvailability);
        Assert.All(sass.Rows.Items, row => Assert.Equal("sass", row.ViewKind));
        Assert.Equal("unavailable", sass.HlslAvailability);
    }

    [Fact]
    public void MissingHlslDoesNotTurnIntoSuccessfulEmptyHotspots()
    {
        var unavailable = SourceHotspotsWrapper.Project(Source(), "hlsl", 0, 0, 20, Stats);
        Assert.False(unavailable.IsSuccess);
        Assert.Equal(ErrorCategory.Unavailable, unavailable.Error!.Category);
        Assert.Equal("wrapper.source_view_unavailable", unavailable.Error.Code);
        var missingOccurrence = SourceHotspotsWrapper.Project(Source(), "dxil", 1, 0, 20, Stats);
        Assert.False(missingOccurrence.IsSuccess);
        Assert.Equal("wrapper.source_view_not_found", missingOccurrence.Error!.Code);
    }

    [Fact]
    public void NoSamplesHasNoFabricatedCoverageAndRemainsAnExplicitEmptyPage()
    {
        var source = Source();
        var rows = source.Rows.Items.Select(row => row.ViewKind == "dxil"
            ? row with { SampleCount = 0, Stalls = [] } : row).ToArray();
        source = source with
        {
            Views = source.Views.Select(view => view.Kind == "dxil"
                ? view with { AttributedSampleCount = 0, SampleAvailability = "empty" } : view).ToArray(),
            Rows = WrapperSupport.Page(rows, 0, int.MaxValue),
        };
        var result = SourceHotspotsWrapper.Project(source, "dxil", 0, 0, 20, Stats);
        Assert.True(result.IsSuccess);
        Assert.Equal(0, result.Value!.View.AttributedSampleCount);
        Assert.Equal("empty", result.Value.View.SampleAvailability);
        Assert.Empty(result.Value.Rows.Items);
        Assert.Equal(0, result.Value.Rows.TotalCount);
        Assert.Null(result.Value.SampleCoveragePercent);
    }

    [Fact]
    public void UnknownSamplesCannotBecomeSuccessfulZeroCoverage()
    {
        var source = Source();
        var rows = source.Rows.Items.Select(row => row.SourceOrdinal == 0
            ? row with { SampleAvailability = "unknown" } : row).ToArray();
        source = source with { Rows = WrapperSupport.Page(rows, 0, int.MaxValue) };
        var result = SourceHotspotsWrapper.Project(source, "dxil", 0, 0, 20, Stats);
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCategory.Unavailable, result.Error!.Category);
        Assert.Equal("wrapper.source_samples_unavailable", result.Error.Code);
    }

    [Fact]
    public void IncompleteOrInconsistentSourceCannotProduceAHotspotRanking()
    {
        var source = Source();
        var partial = source with { Rows = WrapperSupport.Page(source.Rows.Items, 0, 20) };
        Assert.False(SourceHotspotsWrapper.Project(partial, "dxil", 0, 0, 20, Stats).IsSuccess);
        var mismatched = source with
        {
            Views = source.Views.Select(view => view.Kind == "dxil"
                ? view with { AttributedSampleCount = view.AttributedSampleCount + 1 } : view).ToArray(),
        };
        Assert.False(SourceHotspotsWrapper.Project(mismatched, "dxil", 0, 0, 20, Stats).IsSuccess);
    }

    internal static ShaderSourceValue Source()
    {
        // Real bridge capture from probe-0.51. Only names/hash/source text and
        // provider IDs are sanitized; all 965 selected rows and numbers survive.
        using var fixture = FixtureBridge.Load("shader-source-sampled.json");
        var selection = new ShaderSourceSelection(
            new(1773, [0, 2, 12, 71, 0], "1606 - 2474", "Pass01"),
            new(0, [76, 0], "Pixel", "Shader01", "0xa000000000000001", 0, "Pipeline01"),
            0);
        return BridgeProjection.ProjectShaderSource(fixture.RootElement,
            1773, null, selection, 0, int.MaxValue);
    }

    private static readonly WrapperExecutionStats Stats = new(1, 965, 0, 0);
}
