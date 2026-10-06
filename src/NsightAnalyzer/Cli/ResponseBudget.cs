using NsightAnalyzer.Contracts;

namespace NsightAnalyzer.Cli;

/// <summary>
/// Keeps variable-width pageable responses inside the stdout transport bound.
/// This is a transport projection only: it retains a stable prefix of the
/// already-proven facts and advances nextCursor to the first omitted item.
/// </summary>
internal static class ResponseBudget
{
    internal const string PageReducedWarningCode =
        "runtime.response_page_reduced";

    public static OperationResult Fit(
        string operation,
        OperationResult result,
        bool detail,
        bool compact)
    {
        if (!result.IsSuccess ||
            operation != "trace.range-shaders" ||
            result.Value is not RangeShadersValue value ||
            value.Shaders.Items.Count == 0 ||
            Fits(operation, result, detail, compact))
        {
            return result;
        }

        // Row width depends on names, correlation metadata and, in detailed
        // output, instruction mix. Find the largest stable prefix that fits
        // the actual serialized envelope instead of relying on a fixture-sized
        // row estimate. The original page is known not to fit.
        var low = 1;
        var high = value.Shaders.Items.Count - 1;
        OperationResult? best = null;
        while (low <= high)
        {
            var count = low + ((high - low) / 2);
            var candidate = Resize(result, value, count);
            if (Fits(operation, candidate, detail, compact))
            {
                best = candidate;
                low = count + 1;
            }
            else
            {
                high = count - 1;
            }
        }

        // A single pathological row can still exceed the response bound. In
        // that case retain the original result so JsonRenderer emits the
        // existing explicit failure instead of returning an empty/infinite page.
        return best ?? result;
    }

    private static OperationResult Resize(
        OperationResult result,
        RangeShadersValue value,
        int count)
    {
        var page = value.Shaders;
        var items = page.Items.Take(count).ToArray();
        var nextCursor = checked(page.Cursor + items.Length);
        int? next = nextCursor < page.TotalCount ? nextCursor : null;
        var resized = page with
        {
            Items = items,
            ReturnedCount = items.Length,
            Truncated = next is not null,
            NextCursor = next,
        };
        var warning = new OperationWarning(
            PageReducedWarningCode,
            $"The shader page was safely shortened from {page.ReturnedCount} to {items.Length} rows " +
            $"to keep the JSON response within {ContractLimits.MaximumResponseBytes} bytes. " +
            $"Reissue with the same trace, scope, filters and detail options, setting --cursor " +
            $"{next!.Value}; follow each nextCursor until it is absent. The trace and returned " +
            "shader facts were not modified.");
        var warnings = (result.Warnings ?? [])
            .Where(item => item.Code != PageReducedWarningCode)
            .Append(warning)
            .ToArray();
        return result with
        {
            Value = value with { Shaders = resized },
            Warnings = warnings,
        };
    }

    private static bool Fits(
        string operation,
        OperationResult result,
        bool detail,
        bool compact)
    {
        var projected = AgentResponseProjection.Apply(result, detail);
        return JsonRenderer.Render(
            new(operation, SchemaVersion.V2, projected), compact).WithinBound;
    }
}
