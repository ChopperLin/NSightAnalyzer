using System.Text;
using System.Text.Json;
using NsightAnalyzer.Contracts;
using NsightAnalyzer.Wrappers;
using Xunit;

namespace NsightAnalyzer.Tests;

public sealed class BatchTimingTests
{
    [Theory]
    [InlineData("{\"pairs\":[{\"baselineEventOrdinal\":1}]}")]
    [InlineData("{\"pairs\":[{\"targetEventOrdinal\":0,\"targetEventOrdinal\":3,\"baselineEventOrdinal\":1}]}")]
    [InlineData("{\"pairs\":[{\"targetEventOrdinal\":0,\"baselineEventOrdinal\":1},{\"targetEventOrdinal\":0,\"baselineEventOrdinal\":1}]}")]
    [InlineData("{\"pairs\":[]}")]
    [InlineData("{\"pairs\":[{\"targetEventOrdinal\":0,\"baselineEventOrdinal\":1,\"automaticMatching\":true}]}")]
    [InlineData("{\"pairs\":[{\"targetEventOrdinal\":0,\"baselineEventOrdinal\":1,\"sampleGroup\":\"\"}]}")]
    [InlineData("{\"pairs\":[{\"targetEventOrdinal\":0,\"baselineEventOrdinal\":1,\"sampleGroup\":\"  \"}]}")]
    [InlineData("{\"pairs\":[{\"targetEventOrdinal\":0,\"baselineEventOrdinal\":1,\"sampleGroup\":5}]}")]
    public void InvalidOrDuplicatedPairDoesNotSilentlyChooseAScope(string input)
    {
        var result = ReadInput(input);
        Assert.False(result.IsSuccess);
        Assert.Equal("wrapper.timing_pairs_invalid", result.Error!.Code);
    }

    [Fact]
    public void InputByteCountPairCountAndCallerMetadataAreBounded()
    {
        var oversized = new string(' ', BatchTimingWrapper.MaximumInputBytes + 1);
        Assert.False(ReadInput(oversized).IsSuccess);
        var pairs = Enumerable.Range(0, 33).Select(i => new { targetEventOrdinal = i, baselineEventOrdinal = i + 1 });
        Assert.False(ReadInput(JsonSerializer.Serialize(new { pairs })).IsSuccess);
        foreach (var field in new[] { "label", "sampleGroup" })
        {
            var request = "{\"pairs\":[{\"targetEventOrdinal\":0,\"baselineEventOrdinal\":1,\"" +
                field + "\":\"" + new string('x', 121) + "\"}]}";
            Assert.False(ReadInput(request).IsSuccess);
        }
        var boundary = ReadInput("{\"pairs\":[{\"targetEventOrdinal\":0,\"baselineEventOrdinal\":1,\"sampleGroup\":\"" +
            new string('x', 120) + "\"}]}");
        Assert.True(boundary.IsSuccess);
        Assert.Equal(120, Assert.Single(boundary.Value!.Pairs).SampleGroup!.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Utf8WithOrWithoutBomRetainsExternalGroupMetadata(bool bom)
    {
        var result = ReadInput("{\"pairs\":[{\"targetEventOrdinal\":0,\"baselineEventOrdinal\":1," +
            "\"label\":\"正常帧\",\"sampleGroup\":\"同一阶段\"}]}", new UTF8Encoding(bom));
        Assert.True(result.IsSuccess);
        var pair = Assert.Single(result.Value!.Pairs);
        Assert.Equal("正常帧", pair.Label);
        Assert.Equal("同一阶段", pair.SampleGroup);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Utf16ErrorExplainsTheRequiredEncoding(bool bigEndian)
    {
        var result = ReadInput("{\"pairs\":[{\"targetEventOrdinal\":0,\"baselineEventOrdinal\":1}]}",
            new UnicodeEncoding(bigEndian, true));
        Assert.False(result.IsSuccess);
        Assert.Contains("UTF-8", result.Error!.Message);
        Assert.Contains("UTF-16", result.Error.Message);
    }

    [Fact]
    public void InputPagingKeepsOriginalOrdinalsAndVisitsEachPairOnce()
    {
        var pairs = Enumerable.Range(0, 32).Select(i => new TimingPairInput(i, i + 100)).ToArray();
        var first = BatchTimingWrapper.SelectPairs(pairs, 0, 20);
        Assert.Null(WrapperSupport.ValidatePage(first, 0, 20, 32));
        Assert.Equal(20, first.ReturnedCount);
        Assert.Equal(20, first.NextCursor);
        var second = BatchTimingWrapper.SelectPairs(pairs, first.NextCursor!.Value, 20);
        Assert.Null(WrapperSupport.ValidatePage(second, 20, 20, 32));
        Assert.Equal(12, second.ReturnedCount);
        Assert.False(second.Truncated);
        Assert.Null(second.NextCursor);
        Assert.Equal(Enumerable.Range(0, 32),
            first.Items.Concat(second.Items).Select(item => item.SourceOrdinal));
        Assert.Equal(pairs, first.Items.Concat(second.Items).Select(item => item.Pair));
        var exhausted = BatchTimingWrapper.SelectPairs(pairs, 32, 20);
        Assert.Equal(32, exhausted.TotalCount);
        Assert.Empty(exhausted.Items);
        Assert.False(exhausted.Truncated);
    }

    [Fact]
    public void SummariesKeepUnavailableValuesOutOfTheDenominatorAndPreserveSign()
    {
        var rows = new[]
        {
            Pair(0, "PassA", "PassA", 2m, 1m),
            Pair(1, "PassA", "PassA", 4m, 2m),
            Pair(2, "PassA", "PassA", null, 2m),
            Pair(3, "PassA", "DifferentPass", 9m, 1m),
            Pair(4, "PassB", "PassB", 1m, 4m),
        };
        var summaries = BatchTimingWrapper.Summarize(rows);
        var a = Assert.Single(summaries, item => item.EventDescription == "PassA" && item.Field == "duration");
        Assert.Equal(3, a.PairCount);
        Assert.Equal(2, a.ComparableCount);
        Assert.Equal(1, a.UnavailableCount);
        Assert.Equal(3m, a.MedianTargetMilliseconds);
        Assert.Equal(1.5m, a.MedianDeltaMilliseconds);
        Assert.Equal(-3m, Assert.Single(summaries, item => item.EventDescription == "PassB" && item.Field == "duration").MedianDeltaMilliseconds);
    }

    [Fact]
    public void DifferentParentObjectOrThreadContextsRemainSeparateByDefault()
    {
        var pairs = new[]
        {
            Pair(0, "PassA", "PassA", 2m, 1m),
            Pair(1, "PassA", "PassA", 100m, 1m, targetParent: 9),
            Pair(2, "PassA", "PassA", 2m, 1m, baselineParent: 9),
            Pair(3, "PassA", "PassA", 2m, 1m, targetObject: "object1"),
            Pair(4, "PassA", "PassA", 2m, 1m, baselineObject: "object1"),
            Pair(5, "PassA", "PassA", 2m, 1m, targetThread: "thread1"),
            Pair(6, "PassA", "PassA", 2m, 1m, baselineThread: "thread1"),
        };
        var summaries = BatchTimingWrapper.Summarize(pairs).Where(item => item.Field == "duration").ToArray();
        Assert.Equal(7, summaries.Length);
        Assert.All(summaries, item =>
        {
            Assert.Equal(1, item.PairCount);
            Assert.Equal("exactTargetAndBaselineContext", item.GroupingBasis);
            Assert.Null(item.CallerSampleGroup);
            Assert.NotNull(item.TargetContext);
            Assert.NotNull(item.BaselineContext);
        });
        Assert.DoesNotContain(summaries, item => item.MedianDeltaMilliseconds == 50m);
        Assert.Equal(99m, Assert.Single(summaries,
            item => item.TargetContext!.ParentTreePath.SequenceEqual([0, 9])).MedianDeltaMilliseconds);
    }

    [Fact]
    public void CallerGroupExplicitlyCombinesContextsButNeverDifferentNamesOrDepths()
    {
        var pairs = new[]
        {
            Pair(0, "PassA", "PassA", 2m, 1m, group: "same-phase"),
            Pair(1, "PassA", "PassA", 100m, 1m, targetParent: 9, baselineParent: 10,
                targetThread: "thread2", group: "same-phase"),
            Pair(2, "PassB", "PassB", 5m, 1m, group: "same-phase"),
            Pair(3, "PassA", "PassA", 8m, 1m, targetDepth: 3, baselineDepth: 3, group: "same-phase"),
            Pair(4, "PassA", "DifferentPass", 50m, 1m, group: "same-phase"),
            Pair(5, "PassA", "PassA", 50m, 1m, targetDepth: 3, group: "same-phase"),
        };
        var summaries = BatchTimingWrapper.Summarize(pairs).Where(item => item.Field == "duration").ToArray();
        Assert.Equal(3, summaries.Length);
        var combined = Assert.Single(summaries, item => item.EventDescription == "PassA" && item.EventDepth == 2);
        Assert.Equal(2, combined.PairCount);
        Assert.Equal(50m, combined.MedianDeltaMilliseconds);
        Assert.All(summaries, item =>
        {
            Assert.Equal("callerDeclaredSampleGroup", item.GroupingBasis);
            Assert.Equal("same-phase", item.CallerSampleGroup);
            Assert.Null(item.TargetContext);
            Assert.Null(item.BaselineContext);
        });
        Assert.Equal(4, summaries.Sum(item => item.PairCount));
    }

    [Fact]
    public void StatisticsOnlyUseTheSelectedInputPage()
    {
        var input = new[] { new TimingPairInput(0, 1), new TimingPairInput(2, 3) };
        var facts = new[] { Pair(0, "PassA", "PassA", 2m, 1m), Pair(1, "PassA", "PassA", 100m, 1m) };
        var selected = BatchTimingWrapper.SelectPairs(input, 1, 1);
        var pageFacts = selected.Items.Select(item => facts[item.SourceOrdinal]).ToArray();
        var summary = Assert.Single(BatchTimingWrapper.Summarize(pageFacts), item => item.Field == "duration");
        Assert.Equal(1, summary.PairCount);
        Assert.Equal(99m, summary.MedianDeltaMilliseconds);
    }

    private static WrapperValueResult<TimingPairsInput> ReadInput(string input, Encoding? encoding = null)
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, input, encoding ?? new UTF8Encoding(false));
            return BatchTimingWrapper.ReadPairs(path);
        }
        finally { File.Delete(path); }
    }

    private static TimingPairFact Pair(int ordinal, string target, string baseline, decimal? a, decimal? b,
        int targetParent = 2, int baselineParent = 2,
        string? targetObject = "object0", string? baselineObject = "object0",
        string? targetThread = "thread0", string? baselineThread = "thread0",
        int targetDepth = 2, int baselineDepth = 2, string? group = null)
    {
        EventFact Event(string name, int parent, int depth, string? obj, string? thread) =>
            new(new(ordinal, Enumerable.Repeat(0, depth - 1).Append(parent).Append(ordinal).ToArray(), "1-2", name),
                depth, 1, obj, thread, null, null, null, null, null);
        return new(ordinal, "caller-label", new("target", "target-id", Event(target, targetParent, targetDepth, targetObject, targetThread)),
            new("baseline", "baseline-id", Event(baseline, baselineParent, baselineDepth, baselineObject, baselineThread)),
            new[] { "duration", "gpuDuration", "cpuDuration" }.Select(field =>
                new DurationComparisonFact(field, "viewerDisplay",
                    new(null, a, a is null ? "absent" : "parsedDisplay"),
                    new(null, b, b is null ? "absent" : "parsedDisplay"),
                    a is null || b is null ? null : a - b, null)).ToArray(), [], group);
    }
}
