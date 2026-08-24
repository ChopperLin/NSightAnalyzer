using NsightAnalyzer.Contracts;

namespace NsightAnalyzer.Cli;

internal static class EntryPoint
{
    public static async Task<int> RunAsync(string[] args)
    {
        var compact = args.Contains("--compact", StringComparer.Ordinal);
        var parseResult = CommandLine.Parse(args);
        if (!parseResult.IsSuccess)
        {
            var operation = args.FirstOrDefault() ?? "cli";
            var failure = OperationResult.Failure(
                ErrorCategory.InvalidInput,
                "cli.arguments_invalid",
                parseResult.Error ?? "The command line is invalid.");
            return JsonRenderer.Write(new(operation, SchemaVersion.V1, failure), compact)
                ? ExitCodes.InvalidInput
                : ExitCodes.Internal;
        }

        var command = parseResult.Command!;
        try
        {
            var result = await OperationRegistry.ExecuteAsync(command);
            var withinBound = JsonRenderer.Write(
                new(command.Operation, SchemaVersion.V1, result), command.Compact);
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
            return JsonRenderer.Write(new(command.Operation, SchemaVersion.V1, result), command.Compact)
                ? ExitCodes.Internal
                : ExitCodes.Internal;
        }
    }
}
