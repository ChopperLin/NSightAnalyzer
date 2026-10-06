using NsightAnalyzer.Contracts;

namespace NsightAnalyzer.Cli;

internal static class EntryPoint
{
    // An agent that does not yet know this CLI will try these before anything
    // else. Answering with the catalog costs nothing and removes a guaranteed
    // first failure.
    private static readonly string[] HelpTokens =
        ["--help", "-h", "-?", "/?", "help"];

    public static async Task<int> RunAsync(string[] args)
    {
        args = NormalizeHelp(args);
        var compact = !args.Contains("--pretty", StringComparer.Ordinal);

        var parseResult = CommandLine.Parse(args);
        if (!parseResult.IsSuccess)
        {
            var operation = args.FirstOrDefault() ?? "cli";
            var failure = OperationResult.Failure(
                ErrorCategory.InvalidInput,
                "cli.arguments_invalid",
                parseResult.Error ?? "The command line is invalid.");
            return JsonRenderer.Write(new(operation, SchemaVersion.V2, failure), compact)
                ? ExitCodes.InvalidInput
                : ExitCodes.Internal;
        }

        var command = parseResult.Command!;
        try
        {
            using var workspace = WorkspaceEnvironment.Apply(command.Workspace);
            var result = await OperationRegistry.ExecuteAsync(command);
            result = ResponseBudget.Fit(
                command.Operation, result, command.Detail, command.Compact);
            var withinBound = JsonRenderer.Write(
                new(command.Operation, SchemaVersion.V2,
                    AgentResponseProjection.Apply(result, command.Detail)), command.Compact);
            return !withinBound
                ? ExitCodes.Internal
                : result.IsSuccess ? ExitCodes.Success : ExitCodes.From(result.Error!.Category);
        }
        catch (Exception exception)
        {
            var result = OperationResult.Failure(
                ErrorCategory.Internal,
                "internal.unhandled",
                "The operation failed unexpectedly.",
                exception.GetType().Name);
            return JsonRenderer.Write(new(command.Operation, SchemaVersion.V2, result), command.Compact)
                ? ExitCodes.Internal
                : ExitCodes.Internal;
        }
    }

    internal static string[] NormalizeHelp(string[] args)
    {
        if (args.Length > 0 && args[0] == "--version")
        {
            return ["version", .. args.Skip(1)];
        }
        if (args.Length == 0 || args.All(item => item is "--compact" or "--pretty"))
        {
            return ["capabilities", .. args];
        }
        if (HelpTokens.Contains(args[0], StringComparer.OrdinalIgnoreCase))
        {
            var remaining = args.Skip(1).ToArray();
            return remaining.Length > 0 && !remaining[0].StartsWith('-')
                ? ["describe", .. remaining]
                : ["capabilities", .. remaining];
        }
        if (OperationRegistry.IsKnown(args[0]) &&
            args.Skip(1).Any(item => HelpTokens.Contains(item, StringComparer.OrdinalIgnoreCase)))
        {
            return ["describe", args[0], .. HelpOptions(args.Skip(1).ToArray())];
        }
        return args;
    }

    private static IEnumerable<string> HelpOptions(string[] args)
    {
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] is "--pretty" or "--compact") yield return args[index];
            if (args[index] != "--workspace") continue;
            yield return args[index];
            if (index + 1 < args.Length && !args[index + 1].StartsWith('-'))
                yield return args[++index];
        }
    }
}
