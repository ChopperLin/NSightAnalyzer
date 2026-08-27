using NsightAnalyzer.Contracts;
using NsightAnalyzer.Wrappers;
using Xunit;

namespace NsightAnalyzer.Tests;

public sealed class InspectPassContractTests
{
    [Fact]
    public void AvailableInstructionMixRetainsFactsAndCount()
    {
        var facts = new[]
        {
            new RangeInstructionMixFact(1, "FMA", "FP32 Math", null, 3, 5, []),
            new RangeInstructionMixFact(0, "ALU", "Integer", null, 2, 4, []),
        };

        var result = InspectPassWrapper.ProjectInstructionMix(facts, null);

        Assert.Equal("available", result.Availability);
        Assert.Equal(2, result.TotalCount);
        Assert.Equal([0, 1], result.Items!.Select(item => item.SourceOrdinal));
        Assert.Null(result.Error);
    }

    [Fact]
    public void NotLoadedInstructionMixIsNotReportedAsSuccessfulEmptyData()
    {
        var error = new OperationError(
            ErrorCategory.Unavailable,
            "trace.range_instruction_mix_not_loaded",
            "The Viewer did not load Instruction Mix.",
            "model present but empty");

        var result = InspectPassWrapper.ProjectInstructionMix(null, error);

        Assert.Equal("notLoaded", result.Availability);
        Assert.Null(result.TotalCount);
        Assert.Null(result.Items);
        Assert.Same(error, result.Error);
    }

    [Fact]
    public void NotRequestedInstructionMixIsExplicitlyUnavailable()
    {
        var error = new OperationError(
            ErrorCategory.Unavailable,
            "trace.range_instruction_mix_not_requested",
            "Use the explicit atom when this fact family is needed.");

        var result = InspectPassWrapper.ProjectInstructionMix(null, error);

        Assert.Equal("unavailable", result.Availability);
        Assert.Null(result.TotalCount);
        Assert.Null(result.Items);
        Assert.Same(error, result.Error);
    }

    [Fact]
    public void InstructionMixAvailabilityStateMustBeUnambiguous()
    {
        var error = new OperationError(
            ErrorCategory.Unavailable,
            "trace.range_instruction_mix_not_loaded",
            "not loaded");

        Assert.Throws<InvalidOperationException>(() =>
            InspectPassWrapper.ProjectInstructionMix([], error));
        Assert.Throws<InvalidOperationException>(() =>
            InspectPassWrapper.ProjectInstructionMix(null, null));
    }
}
