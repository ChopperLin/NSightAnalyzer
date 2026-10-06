using System.Diagnostics;
using System.Text.Json;

namespace NsightAnalyzer.Adapters.NsightViewer2026_2;

internal sealed record ViewerHostTarget(
    string AdapterName,
    string CompatibilityProfile,
    string FileProductVersion,
    string ProductVersion,
    string ProductBuild,
    string ProductSku,
    string NsightVersion,
    string QtRuntimeVersion,
    string QtCompileVersion,
    string DefaultViewerPath)
{
    public string ApplicationVersion =>
        $"{ProductVersion} (build {ProductBuild}) ({ProductSku})";
}

internal static class ViewerHostTargets
{
    // Preserve the proven 2026.2 decoder when both exact builds are installed.
    // A machine with only 2026.3.1 still works without an explicit --viewer.
    internal static IReadOnlyList<ViewerHostTarget> All { get; } = Load();

    internal static ViewerHostTarget Preferred => All[0];

    internal static string SupportedRuntimeVersions =>
        string.Join(" or ", All.Select(target => target.ProductVersion));

    internal static string ResolveViewerPath(string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return Path.GetFullPath(overridePath);
        }
        return All.FirstOrDefault(target => File.Exists(target.DefaultViewerPath))
            ?.DefaultViewerPath ?? Preferred.DefaultViewerPath;
    }

    internal static IReadOnlyList<ViewerHostTarget> FromFileVersion(FileVersionInfo version) =>
        All.Where(target => string.Equals(
                version.ProductVersion, target.FileProductVersion, StringComparison.Ordinal))
            .ToArray();

    internal static IReadOnlyList<ViewerHostTarget> FromExecutable(string viewerPath)
    {
        if (!File.Exists(viewerPath))
        {
            return [];
        }
        return FromFileVersion(FileVersionInfo.GetVersionInfo(viewerPath));
    }

    internal static bool MatchesRuntime(
        ViewerHostTarget target,
        string? applicationVersion,
        string? qtRuntimeVersion,
        string? qtCompileVersion) =>
        string.Equals(applicationVersion, target.ApplicationVersion, StringComparison.Ordinal) &&
        string.Equals(qtRuntimeVersion, target.QtRuntimeVersion, StringComparison.Ordinal) &&
        string.Equals(qtCompileVersion, target.QtCompileVersion, StringComparison.Ordinal);

    internal static ViewerHostTarget? MatchVerifiedRuntime(
        IReadOnlyList<ViewerHostTarget> candidates,
        string? applicationVersion,
        string? qtRuntimeVersion,
        string? qtCompileVersion,
        string? nsightVersion,
        string? nsightBuild,
        string? compatibilityProfile) =>
        candidates.SingleOrDefault(target =>
            MatchesRuntime(target, applicationVersion, qtRuntimeVersion, qtCompileVersion) &&
            string.Equals(nsightVersion, target.NsightVersion, StringComparison.Ordinal) &&
            string.Equals(nsightBuild, target.ProductBuild, StringComparison.Ordinal) &&
            string.Equals(compatibilityProfile, target.CompatibilityProfile, StringComparison.Ordinal));

    internal static string BridgeVerificationJson(IReadOnlyList<ViewerHostTarget> candidates) =>
        JsonSerializer.Serialize(candidates.Select(target => new
        {
            applicationVersion = target.ApplicationVersion,
            qtRuntimeVersion = target.QtRuntimeVersion,
            qtCompileVersion = target.QtCompileVersion,
            nsightVersion = target.NsightVersion,
            nsightBuild = target.ProductBuild,
            compatibilityProfile = target.CompatibilityProfile,
        }));

    private static IReadOnlyList<ViewerHostTarget> Load()
    {
        using var stream = typeof(ViewerHostTargets).Assembly.GetManifestResourceStream(
            "NsightAnalyzer.ViewerHostTargets.json") ??
            throw new InvalidDataException("The embedded Viewer host target table is missing.");
        var targets = JsonSerializer.Deserialize<ViewerHostTarget[]>(stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        if (targets.Length == 0 ||
            targets.Any(target =>
                string.IsNullOrWhiteSpace(target.CompatibilityProfile) ||
                string.IsNullOrWhiteSpace(target.ApplicationVersion) ||
                !Path.IsPathFullyQualified(target.DefaultViewerPath)) ||
            targets.Select(target => target.ApplicationVersion).Distinct(StringComparer.Ordinal).Count() !=
                targets.Length)
        {
            throw new InvalidDataException("The embedded Viewer host target table is invalid.");
        }
        return Array.AsReadOnly(targets);
    }
}
