using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NsightAnalyzer.Tests;

/// <summary>
/// Loads sanitized bridge output. Fixtures keep every number, row/column
/// structure, occurrence and availability state of the real report; only
/// project-identifying names are replaced. See
/// tools/fixtures/sanitize-bridge-output.py.
/// </summary>
internal static class FixtureBridge
{
    public static JsonDocument Load(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Missing bridge fixture '{name}'.", path);
        }
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    public static JsonDocument LoadMetricCatalog(string name)
    {
        var root = LoadNode(name);
        foreach (var viewNode in root["metricViews"]!.AsArray())
        {
            var view = viewNode!.AsObject();
            var export = view["export"]!.AsObject();
            var headers = export["headers"]!.DeepClone();
            view["export"] = new JsonObject
            {
                ["catalogOnly"] = true,
                ["headers"] = headers,
                ["rowCount"] = export["totalCount"]!.GetValue<int>(),
                ["columnCount"] = headers.AsArray().Count,
                ["totalCountExact"] = true,
                ["truncated"] = false,
            };
        }
        return JsonDocument.Parse(root.ToJsonString());
    }

    public static JsonDocument LoadDurationOrderedRanges(string name)
    {
        var root = LoadNode(name);
        var export = root["eventViews"]![0]!["export"]!.AsObject();
        var ordered = export["nodes"]!.AsArray()
            .Select(node => node!.DeepClone())
            .OrderByDescending(DurationMilliseconds)
            .ThenBy(node => node!["ordinal"]!.GetValue<int>())
            .ToArray();
        export["nodes"] = new JsonArray(ordered);
        export["includeAncestors"] = true;
        export["ancestorNodes"] = new JsonArray();
        export["ancestorCount"] = 0;
        export["sortDurationColumn"] = 10;
        export["nameColumn"] = -1;
        export["nameContains"] = null;
        export["grainColumn"] = -1;
        export["grainPrefixes"] = new JsonArray();
        export["grainSubstrings"] = new JsonArray();
        export["grainMode"] = "include";
        export["withinOrdinal"] = -1;
        export["withinFound"] = false;
        export["withinNode"] = new JsonObject();
        return JsonDocument.Parse(root.ToJsonString());
    }

    public static JsonDocument LoadEventCandidates(string name)
    {
        var root = LoadNode(name);
        var export = root["eventViews"]![0]!["export"]!.AsObject();
        export["nameContains"] = export["nameExact"]!.DeepClone();
        export["nameExact"] = null;
        export["withinOrdinal"] = -1;
        export["withinFound"] = false;
        export["withinNode"] = new JsonObject();
        return JsonDocument.Parse(root.ToJsonString());
    }

    private static JsonObject LoadNode(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Missing bridge fixture '{name}'.", path);
        }
        return JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    }

    private static double DurationMilliseconds(JsonNode node)
    {
        var cells = node["cells"]!.AsArray();
        var display = cells
            .Select(cell => cell!.AsObject())
            .Single(cell => cell["column"]!.GetValue<int>() == 10)["display"]?
            .GetValue<string>();
        if (display is null)
        {
            return double.NegativeInfinity;
        }
        var normalized = display.Replace('µ', 'u').Replace('μ', 'u');
        var match = Regex.Match(
            normalized,
            @"^\s*<?\s*(?<value>\d+(?:\.\d+)?)\s*(?<unit>ns|us|ms|s)\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success || !double.TryParse(
                match.Groups["value"].Value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var value))
        {
            return double.NegativeInfinity;
        }
        return match.Groups["unit"].Value.ToLowerInvariant() switch
        {
            "ns" => value / 1_000_000d,
            "us" => value / 1_000d,
            "ms" => value,
            "s" => value * 1_000d,
            _ => double.NegativeInfinity,
        };
    }
}
