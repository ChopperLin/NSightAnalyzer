using System.Text.Json;
using System.Text.Json.Serialization;
using NsightAnalyzer.Contracts;

namespace NsightAnalyzer.Cli;

internal static class JsonRenderer
{
    private static readonly JsonSerializerOptions PrettyOptions = CreateOptions(false);
    private static readonly JsonSerializerOptions CompactOptions = CreateOptions(true);

    public static bool Write(OperationEnvelope envelope, bool compact = true)
    {
        var rendered = Render(envelope, compact);
        Console.Out.WriteLine(rendered.Json);
        return rendered.WithinBound;
    }

    internal static (string Json, bool WithinBound) Render(
        OperationEnvelope envelope, bool compact = true)
    {
        if (envelope.Result.Error is { } error)
        {
            envelope = envelope with
            {
                Result = envelope.Result with
                {
                    Error = error with { Recovery = error.Recovery ?? ErrorRecoveryPolicy.For(envelope.Operation, error) },
                },
            };
        }
        var options = compact ? CompactOptions : PrettyOptions;
        var json = JsonSerializer.Serialize(envelope, options);
        if (System.Text.Encoding.UTF8.GetByteCount(json) <=
            ContractLimits.MaximumResponseBytes)
        {
            return (json, true);
        }

        var boundedFailure = new OperationEnvelope(
            envelope.Operation,
            envelope.SchemaVersion,
            OperationResult.Failure(
                ErrorCategory.Internal,
                "runtime.response_limit_exceeded",
                "The serialized response exceeded the hard output bound.",
                $"maximumBytes={ContractLimits.MaximumResponseBytes}"));
        var renderedFailure = Render(boundedFailure, compact);
        return (renderedFailure.Json, false);
    }

    private static JsonSerializerOptions CreateOptions(bool compact)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = !compact,
        };
        options.Converters.Add(
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }
}
