using System.Text.Json;
using System.Text.Json.Serialization;
using NsightAnalyzer.Contracts;

namespace NsightAnalyzer.Cli;

internal static class JsonRenderer
{
    private static readonly JsonSerializerOptions PrettyOptions = CreateOptions(false);
    private static readonly JsonSerializerOptions CompactOptions = CreateOptions(true);

    public static bool Write(OperationEnvelope envelope, bool compact)
    {
        var options = compact ? CompactOptions : PrettyOptions;
        var json = JsonSerializer.Serialize(envelope, options);
        var boundRepresentation = compact
            ? JsonSerializer.Serialize(envelope, PrettyOptions)
            : json;
        if (System.Text.Encoding.UTF8.GetByteCount(boundRepresentation) <=
            ContractLimits.MaximumResponseBytes)
        {
            Console.Out.WriteLine(json);
            return true;
        }

        var boundedFailure = new OperationEnvelope(
            envelope.Operation,
            envelope.SchemaVersion,
            OperationResult.Failure(
                ErrorCategory.Internal,
                "runtime.response_limit_exceeded",
                "The serialized response exceeded the hard output bound.",
                $"maximumBytes={ContractLimits.MaximumResponseBytes}; retry with a smaller page"));
        Console.Out.WriteLine(JsonSerializer.Serialize(boundedFailure, options));
        return false;
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
