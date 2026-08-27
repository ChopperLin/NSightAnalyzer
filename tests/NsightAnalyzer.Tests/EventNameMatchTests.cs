using NsightAnalyzer.Adapters.NsightViewer2026_2;
using Xunit;

namespace NsightAnalyzer.Tests;

/// <summary>
/// Matching a name inside the decoder is what makes a large report usable: the
/// alternative re-traverses every event once per page. The risk it introduces
/// is that a filtered page still has to describe where its matches sit, since
/// the filter removes exactly the rows that would say so.
/// </summary>
public sealed class EventNameMatchTests
{
    private const string Name = "Pass01";

    [Fact]
    public void MatchesCarryTheFullAncestorChainOfEveryMatch()
    {
        using var bridge = FixtureBridge.Load("event-name-matches-pass01.json");

        var value = BridgeProjection.ProjectEventNameMatches(
            bridge.RootElement, 0, 500, Name, contains: false);

        Assert.NotEmpty(value.Matches.Items);
        foreach (var match in value.Matches.Items)
        {
            // Every strict prefix of the match's path must be present, or the
            // caller cannot rebuild the chain it was never allowed to scan.
            for (var depth = 1; depth < match.Key.TreePath.Count; depth++)
            {
                var prefix = match.Key.TreePath.Take(depth).ToArray();
                Assert.Contains(
                    value.Ancestors,
                    ancestor => ancestor.Key.TreePath.SequenceEqual(prefix));
            }
        }
    }

    [Fact]
    public void AncestorsAreNotCountedAsMatches()
    {
        using var bridge = FixtureBridge.Load("event-name-matches-pass01.json");

        var value = BridgeProjection.ProjectEventNameMatches(
            bridge.RootElement, 0, 500, Name, contains: false);

        // Occurrence indexes into matches. If context rows were mixed in, every
        // --event-occurrence the caller passes would address the wrong event.
        Assert.All(value.Matches.Items, match =>
            Assert.Equal(Name, match.Key.Description));
        Assert.Equal(value.Matches.Items.Count, value.Matches.TotalCount);
        Assert.DoesNotContain(
            value.Ancestors,
            ancestor => value.Matches.Items.Any(match =>
                match.Key.PreorderOrdinal == ancestor.Key.PreorderOrdinal));
    }

    [Fact]
    public void OrdinalsRemainTruePreorderOrdinals()
    {
        using var bridge = FixtureBridge.Load("event-name-matches-pass01.json");

        var value = BridgeProjection.ProjectEventNameMatches(
            bridge.RootElement, 0, 500, Name, contains: false);

        // Renumbering to the filtered sequence would make a resolved ordinal
        // unusable against every other operation.
        Assert.All(
            value.Matches.Items.Concat(value.Ancestors),
            fact => Assert.Equal(fact.Depth + 1, fact.Key.TreePath.Count));
        Assert.True(value.TotalEventCount > value.Matches.TotalCount);
        Assert.Contains(
            value.Matches.Items,
            match => match.Key.PreorderOrdinal > value.Matches.TotalCount);
    }

    [Fact]
    public void RefusesAResponseThatAppliedADifferentName()
    {
        using var bridge = FixtureBridge.Load("event-name-matches-pass01.json");

        // Silently accepting this would return one name's rows as another's.
        Assert.Throws<BridgeSchemaException>(() =>
            BridgeProjection.ProjectEventNameMatches(
                bridge.RootElement, 0, 500, "SomeOtherPass", contains: false));

        // The exact and substring filters are different settings; reading an
        // exact response as a substring one would misreport how it was matched.
        Assert.Throws<BridgeSchemaException>(() =>
            BridgeProjection.ProjectEventNameMatches(
                bridge.RootElement, 0, 500, Name, contains: true));
    }

    [Fact]
    public void RefusesAnUnfilteredResponse()
    {
        // Without the name filter this is the first page of the whole trace,
        // which would look like a complete match set for any name asked about.
        using var bridge = FixtureBridge.Load("events-page0.json");

        Assert.Throws<BridgeSchemaException>(() =>
            BridgeProjection.ProjectEventNameMatches(
                bridge.RootElement, 0, 100, Name, contains: false));
    }
}
