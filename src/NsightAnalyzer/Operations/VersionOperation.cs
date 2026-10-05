using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NsightAnalyzer.Contracts;

namespace NsightAnalyzer.Operations;

internal static class VersionOperation
{
    private const long MaximumManifestBytes = 128 * 1024;
    private const long MaximumPackageBytes = 64 * 1024 * 1024;

    public static OperationResult Execute() => OperationResult.Success(Describe());

    public static ToolVersionValue Describe()
    {
        var assembly = typeof(VersionOperation).Assembly;
        var build = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString() ?? "unknown";
        var revision = build.Contains('+') ? build[(build.IndexOf('+') + 1)..] : "unknown";
        string assemblyHash;
        try
        {
            assemblyHash = HashFile(assembly.Location);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new("NSightAnalyzer", build, revision, "unavailable", "assemblySha256", "unreadable");
        }
        var fallback = new ToolVersionValue("NSightAnalyzer", build, revision, assemblyHash, "assemblySha256", "unpackaged");
        var packageDirectory = Path.GetDirectoryName(assembly.Location)!;
        var manifestPath = Path.Combine(packageDirectory, "PACKAGE.json");
        if (!File.Exists(manifestPath)) return fallback;
        try
        {
            using var manifest = File.OpenRead(manifestPath);
            if (manifest.Length is <= 0 or > MaximumManifestBytes)
                return fallback with { PackageState = "manifestInvalid" };
            using var document = JsonDocument.Parse(manifest);
            var root = document.RootElement;
            var declaredRevision = root.GetProperty("gitRevision").GetString();
            var expectedFingerprint = root.GetProperty("contentFingerprint").GetString();
            var files = root.GetProperty("files");
            if (string.IsNullOrWhiteSpace(declaredRevision) || files.ValueKind != JsonValueKind.Array ||
                files.GetArrayLength() is < 1 or > 128)
                return fallback with { PackageState = "manifestInvalid" };

            var observedRows = new List<string>();
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var matches = true;
            long totalBytes = 0;
            foreach (var file in files.EnumerateArray())
            {
                var relative = file.GetProperty("path").GetString()!;
                if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) ||
                    relative.IndexOfAny(['\r', '\n', '\t']) >= 0 || !paths.Add(relative))
                    return fallback with { PackageState = "manifestInvalid" };
                var fullPath = Path.GetFullPath(Path.Combine(packageDirectory, relative));
                if (!fullPath.StartsWith(packageDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return fallback with { PackageState = "manifestInvalid" };
                var info = new FileInfo(fullPath);
                if (!info.Exists) return fallback with { Revision = declaredRevision, PackageState = "filesMissing" };
                totalBytes += info.Length;
                if (totalBytes > MaximumPackageBytes)
                    return fallback with { PackageState = "manifestInvalid" };
                var observedHash = HashFile(fullPath);
                matches &= info.Length == file.GetProperty("size").GetInt64() &&
                    observedHash == file.GetProperty("sha256").GetString();
                observedRows.Add(relative.Replace('\\', '/') + "\t" +
                    info.Length.ToString(CultureInfo.InvariantCulture) + "\t" + observedHash);
            }
            if (!paths.Contains(Path.GetFileName(assembly.Location)))
                return fallback with { PackageState = "manifestInvalid" };
            observedRows.Sort(StringComparer.Ordinal);
            var fingerprint = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(string.Join("\n", observedRows)))).ToLowerInvariant();
            return new("NSightAnalyzer", build, declaredRevision, fingerprint, "packageFilesSha256",
                matches && fingerprint == expectedFingerprint ? "verified" : "contentMismatch");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or
                                         KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException)
        {
            return fallback with { PackageState = "manifestInvalid" };
        }
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > MaximumPackageBytes)
            throw new InvalidDataException("The package file exceeds the bounded fingerprint input size.");
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
