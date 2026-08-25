using System.Text.Json;

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
}
