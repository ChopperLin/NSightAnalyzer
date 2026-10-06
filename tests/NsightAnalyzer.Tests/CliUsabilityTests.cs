using System.Text.Json;
using NsightAnalyzer.Adapters.NsightViewer2026_2;
using NsightAnalyzer.Cli;
using NsightAnalyzer.Contracts;
using NsightAnalyzer.Operations;
using Xunit;

namespace NsightAnalyzer.Tests;

[CollectionDefinition("CliProcessEnvironment", DisableParallelization = true)]
public sealed class CliProcessEnvironmentCollection;

[Collection("CliProcessEnvironment")]
public sealed class CliUsabilityTests
{
    [Fact]
    public void PublishedRequirementsConstructEveryOperationWithoutReadingProse()
    {
        foreach (var descriptor in Descriptors())
        {
            var arguments = MinimalArguments(descriptor);
            var parsed = CommandLine.Parse(arguments.ToArray());
            Assert.True(parsed.IsSuccess, descriptor.Id + ": " + parsed.Error);
            Assert.Contains(descriptor.Parameters!, item => item.Name == "--workspace");
        }
    }

    [Fact]
    public void EveryDeclaredSingleValueOptionRejectsRepetitionBeforeExecution()
    {
        foreach (var descriptor in Descriptors())
            foreach (var parameter in descriptor.Parameters!.Where(item => item.Name.StartsWith("--") && !item.Repeatable))
            {
                var arguments = MinimalArguments(descriptor);
                AddParameter(arguments, parameter);
                AddParameter(arguments, parameter);
                var parsed = CommandLine.Parse(arguments.ToArray());
                Assert.False(parsed.IsSuccess, descriptor.Id + " " + parameter.Name);
                Assert.Contains(parameter.Name + " cannot be repeated", parsed.Error);
            }
    }

    [Fact]
    public void ExplicitlyRepeatableFiltersStillWork()
    {
        Assert.True(CommandLine.Parse(["trace.range-metrics", "capture.ngfx-gputrace", "--event-ordinal", "7",
            "--table", "Registers", "--table", "Occupancy"]).IsSuccess);
        Assert.True(CommandLine.Parse(["trace.range-counters", "capture.ngfx-gputrace", "--event-ordinal", "7",
            "--counter", "A", "--counter", "B", "--range", "Pass 1", "--range", "Pass 2"]).IsSuccess);
    }

    [Theory]
    [InlineData("--pretty", "--compact")]
    [InlineData("--compact", "--pretty")]
    [InlineData("--compact", "--compact")]
    [InlineData("--pretty", "--pretty")]
    public void FormattingCannotSilentlyOverrideEarlierOptions(string first, string second)
    {
        Assert.False(CommandLine.Parse(["capabilities", first, second]).IsSuccess);
        Assert.False(CommandLine.Parse(EntryPoint.NormalizeHelp(["inspect-pass", "--help", first, second])).IsSuccess);
    }

    [Fact]
    public void UndeclaredOptionsCannotBeSilentlyIgnored()
    {
        var result = CommandLine.Parse(["trace.info", "capture.ngfx-gputrace", "--event-name-mode", "exact"]);
        Assert.False(result.IsSuccess);
        Assert.Contains("--event-name-mode", result.Error);
    }

    [Fact]
    public void ConditionalHelpMatchesComparisonAndSearchParsing()
    {
        var compare = Describe("compare-ranges");
        Assert.Contains(compare.Constraints!, item => item.Kind == "requiresValue" &&
            item.Parameters.Contains("--top-shaders") && item.RequiredParameter == "--sections" && item.RequiredValue == "shaders");
        string[] baseArguments = ["compare-ranges", "capture.ngfx-gputrace", "--event-ordinal", "7", "--baseline-event-ordinal", "8"];
        Assert.False(CommandLine.Parse([.. baseArguments, "--top-shaders", "5"]).IsSuccess);
        Assert.True(CommandLine.Parse([.. baseArguments, "--sections", "metrics,shaders", "--top-shaders", "5"]).IsSuccess);
        var timing = CommandLine.Parse([.. baseArguments, "--sections", "timing"]);
        Assert.Equal(RangeSections.Timing, timing.Command!.Sections);
        Assert.False(CommandLine.Parse([.. baseArguments, "--sections", "timing", "--limit", "1"]).IsSuccess);

        var metrics = Describe("find-metrics");
        Assert.Contains(metrics.Constraints!, item => item.Kind == "exactlyOneOf" &&
            item.Parameters.SequenceEqual(["--name-contains", "--source-ordinal"]));
        Assert.False(CommandLine.Parse(["find-metrics", "capture.ngfx-gputrace", "--event-ordinal", "7"]).IsSuccess);
        Assert.False(CommandLine.Parse(["find-metrics", "capture.ngfx-gputrace", "--event-ordinal", "7",
            "--name-contains", "warp", "--source-ordinal", "3"]).IsSuccess);
        Assert.True(CommandLine.Parse(["find-metrics", "capture.ngfx-gputrace", "--event-ordinal", "7",
            "--source-ordinal", "3"]).IsSuccess);
    }

    [Fact]
    public void SourceHotspotsRequireAnExplicitViewAndExactShaderScope()
    {
        string[] arguments = ["find-source-hotspots", "capture.ngfx-gputrace", "--event-ordinal", "7",
            "--shader-stage", "Compute", "--shader-hash", "0x0123456789abcdef"];
        Assert.False(CommandLine.Parse(arguments).IsSuccess);
        var result = CommandLine.Parse([.. arguments, "--view", "dxil", "--view-occurrence", "1"]);
        Assert.True(result.IsSuccess);
        Assert.Equal("dxil", result.Command!.SourceView);
        Assert.Equal(1, result.Command.SourceViewOccurrence);
        Assert.Equal(0, result.Command.ShaderOccurrence ?? 0);
    }

    [Fact]
    public void BatchTimingRequiresCallerPairsAndRejectsUnrelatedScopeOptions()
    {
        Assert.False(CommandLine.Parse(["compare-timings", "capture.ngfx-gputrace"]).IsSuccess);
        var parsed = CommandLine.Parse(["compare-timings", "capture.ngfx-gputrace", "--pairs-file", "pairs.json",
            "--baseline-trace", "baseline.ngfx-gputrace", "--limit", "5"]);
        Assert.True(parsed.IsSuccess);
        Assert.Equal("pairs.json", parsed.Command!.PairsFile);
        Assert.Equal("baseline.ngfx-gputrace", parsed.Command.BaselineTracePath);
        Assert.False(CommandLine.Parse(["compare-timings", "capture.ngfx-gputrace", "--pairs-file", "pairs.json",
            "--event-ordinal", "7"]).IsSuccess);
    }

    [Fact]
    public void RecoveryTargetsTheUnavailableFamilyWithoutAnUnrelatedScopeSearch()
    {
        var ambiguous = ErrorRecoveryPolicy.For("compare-ranges",
            new(ErrorCategory.Unavailable, "wrapper.shader_comparison_ambiguous", "ambiguous"));
        Assert.Equal("trace.range-shaders", ambiguous.Operation);
        var setup = ErrorRecoveryPolicy.For("trace.info",
            new(ErrorCategory.Unavailable, "viewer.bridge_not_installed", "missing"));
        Assert.Equal("doctor", setup.Operation);
        var oversized = ErrorRecoveryPolicy.For("inspect-pass",
            new(ErrorCategory.Internal, "runtime.response_limit_exceeded", "too large"));
        Assert.Null(oversized.Parameter); // A shaders-only request does not accept --limit.
        Assert.Equal("reducePageOrShaderCount", oversized.Action);
    }

    [Fact]
    public void WorkspaceMustBeAbsoluteAndSurvivesHelpAndVersionNormalization()
    {
        Assert.False(CommandLine.Parse(["doctor", "--workspace", "relative"]).IsSuccess);
        var workspace = Path.Combine(Path.GetTempPath(), "nsa task workspace");
        var help = CommandLine.Parse(EntryPoint.NormalizeHelp(["find-ranges", "--help", "--workspace", workspace]));
        Assert.Equal(Path.GetFullPath(workspace), help.Command!.Workspace);
        var version = CommandLine.Parse(EntryPoint.NormalizeHelp(["--version", "--workspace", workspace]));
        Assert.Equal("version", version.Command!.Operation);
        Assert.Equal(Path.GetFullPath(workspace), version.Command.Workspace);
    }

    [Fact]
    public void WorkspaceAnchorsSessionsAcrossWorkingDirectoriesAndRestoresEnvironment()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "nsa workspace " + Guid.NewGuid().ToString("N"));
        var beforeRun = Environment.GetEnvironmentVariable("NSIGHT_ANALYZER_RUN_ROOT");
        var beforeSession = Environment.GetEnvironmentVariable("NSIGHT_ANALYZER_SESSION_ROOT");
        var beforeCwd = Environment.CurrentDirectory;
        try
        {
            using (WorkspaceEnvironment.Apply(workspace))
            {
                Environment.CurrentDirectory = Path.GetTempPath();
                var sessionRoot = ViewerSessionTransport.ResolveSessionRoot();
                Environment.CurrentDirectory = AppContext.BaseDirectory;
                Assert.Equal(sessionRoot, ViewerSessionTransport.ResolveSessionRoot());
                Assert.Equal(Path.Combine(workspace, ".local", "sessions"), sessionRoot);
                Assert.Equal(Path.Combine(workspace, ".local", "runs"), ViewerSessionTransport.ResolveRunRoot());
                Assert.False(Directory.Exists(workspace));
            }
            Assert.Equal(beforeRun, Environment.GetEnvironmentVariable("NSIGHT_ANALYZER_RUN_ROOT"));
            Assert.Equal(beforeSession, Environment.GetEnvironmentVariable("NSIGHT_ANALYZER_SESSION_ROOT"));
        }
        finally
        {
            Environment.CurrentDirectory = beforeCwd;
        }
    }

    [Fact]
    public void DoctorReportsMissingDependenciesWithoutClaimingRuntimeValidationOrWritingWorkspace()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "nsa doctor " + Guid.NewGuid().ToString("N"));
        using var scope = WorkspaceEnvironment.Apply(workspace);
        var viewer = Path.Combine(workspace, "missing-viewer.exe");
        var result = DoctorOperation.Execute(viewer);
        Assert.True(result.IsSuccess);
        var value = Assert.IsType<DoctorValue>(result.Value);
        Assert.False(value.LocalPrerequisitesPresent);
        Assert.Equal("notChecked", value.RuntimeValidation);
        Assert.Contains(value.Checks, item => item.Component == "viewerExecutable" && item.State == "missing" &&
            item.Path == viewer && item.Expected == "2026.2.0.0 or 2026.3.1.0");
        Assert.Contains(value.Checks, item => item.Component == "viewerRuntime" && item.State == "notChecked" &&
            item.Expected.Contains("37991608"));
        Assert.Equal(Path.Combine(workspace, ".local", "sessions"), value.SessionRoot);
        Assert.False(Directory.Exists(workspace));
    }

    [Fact]
    public void ViewerCompatibilityTableContainsOnlyExactVerifiedHosts()
    {
        var viewer2026_2 = Assert.Single(ViewerHostTargets.All,
            target => target.ProductBuild == "37991608");
        var viewer2026_3 = Assert.Single(ViewerHostTargets.All,
            target => target.ProductBuild == "38722833");
        Assert.Equal("2026.2.0.0 (build 37991608) (public-release)", viewer2026_2.ApplicationVersion);
        Assert.Equal("6.8.1", viewer2026_2.QtRuntimeVersion);
        Assert.Equal("2026.3.1.0 (build 38722833) (public-release)", viewer2026_3.ApplicationVersion);
        Assert.Equal("6.10.2", viewer2026_3.QtRuntimeVersion);
        Assert.All(ViewerHostTargets.All, target =>
        {
            Assert.Equal("6.8.1", target.QtCompileVersion);
            Assert.Equal("NsightViewerGpuTraceSemanticV1", target.CompatibilityProfile);
            Assert.DoesNotContain('*', target.ApplicationVersion);
        });
        Assert.Equal(ViewerHostTargets.All.Count,
            ViewerHostTargets.All.Select(target => target.ApplicationVersion).Distinct().Count());
        foreach (var target in ViewerHostTargets.All)
        {
            Assert.True(ViewerHostTargets.MatchesRuntime(
                target, target.ApplicationVersion, target.QtRuntimeVersion, target.QtCompileVersion));
            Assert.False(ViewerHostTargets.MatchesRuntime(
                target, target.ApplicationVersion.Replace(target.ProductBuild, "other-build"),
                target.QtRuntimeVersion, target.QtCompileVersion));
            Assert.False(ViewerHostTargets.MatchesRuntime(
                target, target.ApplicationVersion, "other-qt", target.QtCompileVersion));
        }
    }

    [Fact]
    public void OneSemanticProfileCanSelectMultipleExactPatchBuildsAtRuntime()
    {
        var existing = ViewerHostTargets.All.Single(
            target => target.ProductBuild == "38722833");
        var patch = existing with
        {
            ProductVersion = "2026.3.1.1",
            ProductBuild = "future-tested-build",
            NsightVersion = "2026.3.1.1",
            DefaultViewerPath = @"C:\verified\2026.3.1.1\ngfx-ui.exe",
        };
        ViewerHostTarget[] candidates = [existing, patch];

        var selected = ViewerHostTargets.MatchVerifiedRuntime(
            candidates,
            patch.ApplicationVersion,
            patch.QtRuntimeVersion,
            patch.QtCompileVersion,
            patch.NsightVersion,
            patch.ProductBuild,
            patch.CompatibilityProfile);
        using var bridgeTargets = JsonDocument.Parse(
            ViewerHostTargets.BridgeVerificationJson(candidates));

        Assert.Same(patch, selected);
        Assert.Equal(2, bridgeTargets.RootElement.GetArrayLength());
    }

    [Fact]
    public void DoctorReportsAFilesystemRootAsMissingInsteadOfThrowing()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;

        var result = DoctorOperation.Execute(root);

        Assert.True(result.IsSuccess);
        var value = Assert.IsType<DoctorValue>(result.Value);
        Assert.False(value.LocalPrerequisitesPresent);
        Assert.Contains(value.Checks, item => item.Component == "viewerExecutable" &&
            item.State == "missing" && item.Path == root);
    }

    [Fact]
    public void VersionIdentifiesTheLoadedBuildWithoutViewer()
    {
        var version = Assert.IsType<ToolVersionValue>(VersionOperation.Execute().Value);
        Assert.Equal("NSightAnalyzer", version.Product);
        Assert.NotEmpty(version.Build);
        Assert.Matches("^[0-9a-f]{64}$", version.ContentFingerprint);
        Assert.Equal("assemblySha256", version.FingerprintScope);
        Assert.Equal("unpackaged", version.PackageState);
    }

    private static OperationDescriptor Describe(string operation) =>
        Assert.IsType<OperationDescriptor>(OperationRegistry.DescribeOperation(operation).Value);

    private static IEnumerable<OperationDescriptor> Descriptors()
    {
        var value = OperationRegistry.Describe(detail: true).Value!;
        var page = value.GetType().GetProperty("operations")!.GetValue(value)!;
        var descriptors = ((IReadOnlyList<object>)page.GetType().GetProperty("Items")!.GetValue(page)!)
            .Cast<OperationDescriptor>();
        return descriptors.Append(Describe("viewer-session.close"));
    }

    private static List<string> MinimalArguments(OperationDescriptor descriptor)
    {
        var arguments = new List<string> { descriptor.Id };
        foreach (var parameter in descriptor.Parameters!.Where(item => item.Required))
            AddParameter(arguments, parameter);
        foreach (var constraint in descriptor.Constraints ?? [])
        {
            if (constraint.Kind != "exactlyOneOf" || constraint.Parameters.Any(arguments.Contains)) continue;
            AddParameter(arguments, descriptor.Parameters!.Single(item => item.Name == constraint.Parameters[0]));
        }
        return arguments;
    }

    private static void AddParameter(List<string> arguments, OperationParameter parameter)
    {
        if (parameter.Name.StartsWith("--")) arguments.Add(parameter.Name);
        if (parameter.ValueKind == "flag") return;
        arguments.Add(parameter.Name switch
        {
            "<trace>" => "capture.ngfx-gputrace",
            "<operation>" => "trace.info",
            "--workspace" => Path.Combine(Path.GetTempPath(), "nsa workspace"),
            "--shader-hash" => "0x0123456789abcdef",
            "--shader-stage" => "Compute",
            "--event-path" => "0.1",
            _ when parameter.AllowedValues?.Count > 0 => parameter.AllowedValues[0],
            _ when parameter.ValueKind == "integer" => (parameter.Minimum ?? 0).ToString(),
            _ => "example",
        });
    }
}
