using NsightAnalyzer.Cli;
using NsightAnalyzer.Contracts;
using Xunit;

namespace NsightAnalyzer.Tests;

/// <summary>
/// The operation catalog is the only discovery surface an agent has. It must be
/// constructible into a valid invocation without parsing prose or learning a
/// convention by trial and error.
/// </summary>
public sealed class OperationCatalogTests
{
    private static IReadOnlyList<OperationDescriptor> Descriptors()
    {
        var value = OperationRegistry.Describe().Value;
        var operations = value!.GetType().GetProperty("operations")!.GetValue(value);
        return (IReadOnlyList<OperationDescriptor>)
            operations!.GetType().GetProperty("Items")!.GetValue(operations)!;
    }

    [Fact]
    public void EveryPublicOperationDeclaresParameters()
    {
        Assert.All(Descriptors(), descriptor =>
        {
            Assert.NotNull(descriptor.Parameters);
            Assert.NotEmpty(descriptor.Parameters!);
        });
    }

    [Fact]
    public void EveryParameterIsSelfDescribing()
    {
        foreach (var parameter in Descriptors().SelectMany(d => d.Parameters!))
        {
            Assert.False(string.IsNullOrWhiteSpace(parameter.Name));
            Assert.False(string.IsNullOrWhiteSpace(parameter.ValueKind));
            Assert.False(string.IsNullOrWhiteSpace(parameter.Description));
        }
    }

    [Theory]
    [InlineData("resolve-event", "--event-occurrence")]
    [InlineData("trace.range-shaders", "--shader-occurrence")]
    [InlineData("trace.shader-profile", "--shader-occurrence")]
    public void OccurrenceParametersStateTheirBase(string operation, string name)
    {
        var parameter = Descriptors()
            .Single(descriptor => descriptor.Id == operation)
            .Parameters!
            .Single(candidate => candidate.Name == name);

        // Zero- versus one-based is the single most likely first-call mistake,
        // so the catalog has to answer it without a failed round trip.
        Assert.Contains("zero-based", parameter.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ScopedOperationsMarkTheirMutuallyExclusiveSelectors()
    {
        var descriptor = Descriptors().Single(item => item.Id == "trace.range-metrics");

        var scoped = descriptor.Parameters!
            .Where(parameter => parameter.ExclusiveGroup is not null)
            .Select(parameter => parameter.Name)
            .ToArray();

        Assert.Equal(["--event-ordinal", "--event-path"], scoped);
    }

    [Fact]
    public void PagingParametersDeclareTheirBounds()
    {
        var limit = Descriptors()
            .Single(descriptor => descriptor.Id == "trace.events")
            .Parameters!
            .Single(parameter => parameter.Name == "--limit");

        Assert.Equal(1, limit.Minimum);
        Assert.Equal(ContractLimits.MaximumPageLimit, limit.Maximum);
    }
}

public sealed class AgentInterfaceCommandLineTests
{
    [Fact]
    public void FindRangesAcceptsCompactDiscoveryFilters()
    {
        var parsed = CommandLine.Parse([
            "find-ranges",
            "trace.ngfx-gputrace",
            "--name-contains", "shadow",
            "--grain", "marker",
            "--limit", "20",
        ]);

        Assert.True(parsed.IsSuccess, parsed.Error);
        Assert.Equal("shadow", parsed.Command!.RangeNameContains);
        Assert.Equal("marker", parsed.Command.Grain);
        Assert.Equal(20, parsed.Command.Limit);
    }

    [Fact]
    public void ShaderProfileRequiresStageAndHash()
    {
        var missingStage = CommandLine.Parse([
            "trace.shader-profile",
            "trace.ngfx-gputrace",
            "--event-ordinal", "42",
            "--shader-hash", "0x0123456789abcdef",
        ]);
        var complete = CommandLine.Parse([
            "trace.shader-profile",
            "trace.ngfx-gputrace",
            "--event-ordinal", "42",
            "--shader-stage", "Pixel",
            "--shader-hash", "0x0123456789abcdef",
        ]);

        Assert.False(missingStage.IsSuccess);
        Assert.True(complete.IsSuccess, complete.Error);
    }
}

/// <summary>
/// Substring matching exists to spare an agent from guessing a marker's exact
/// spelling. It must never turn that convenience into a silent choice between
/// distinct events.
/// </summary>
public sealed class EventNameModeTests
{
    private static IReadOnlyList<OperationParameter> ResolveEventParameters()
    {
        var value = OperationRegistry.Describe().Value;
        var operations = value!.GetType().GetProperty("operations")!.GetValue(value);
        var items = (IReadOnlyList<OperationDescriptor>)
            operations!.GetType().GetProperty("Items")!.GetValue(operations)!;
        return items.Single(descriptor => descriptor.Id == "resolve-event").Parameters!;
    }

    [Fact]
    public void MatchModeDefaultsToExact()
    {
        var mode = ResolveEventParameters()
            .Single(parameter => parameter.Name == "--event-name-mode");

        Assert.Equal("exact", mode.Default);
        Assert.Equal(["exact", "contains"], mode.AllowedValues);
        Assert.False(mode.Required);
    }

    [Fact]
    public void MatchModeDocumentsItsAmbiguityRefusal()
    {
        var mode = ResolveEventParameters()
            .Single(parameter => parameter.Name == "--event-name-mode");

        // The refusal is the whole safety property; it has to be discoverable
        // before the caller relies on contains.
        Assert.Contains("refused", mode.Description, StringComparison.OrdinalIgnoreCase);
    }
}
