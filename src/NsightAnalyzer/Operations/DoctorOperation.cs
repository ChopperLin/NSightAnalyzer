using System.Diagnostics;
using System.Runtime.InteropServices;
using NsightAnalyzer.Adapters.NsightViewer2026_2;
using NsightAnalyzer.Contracts;

namespace NsightAnalyzer.Operations;

internal static class DoctorOperation
{
    public static OperationResult Execute(string? viewerPathOverride)
    {
        string viewerPath;
        string runRoot;
        string sessionRoot;
        try
        {
            viewerPath = Path.GetFullPath(string.IsNullOrWhiteSpace(viewerPathOverride)
                ? ViewerProbeRunner.DefaultViewerPath : viewerPathOverride);
            runRoot = ViewerSessionTransport.ResolveRunRoot();
            sessionRoot = ViewerSessionTransport.ResolveSessionRoot();
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return OperationResult.Failure(ErrorCategory.InvalidInput, "doctor.path_invalid",
                "A Viewer or workspace environment path is invalid.", exception.GetType().Name);
        }

        var checks = new List<DependencyCheck>
        {
            new("platform", OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64
                    ? "verified" : "mismatch", "Windows x64",
                $"{RuntimeInformation.OSDescription}; process={RuntimeInformation.ProcessArchitecture}"),
            new("dotnetRuntime", Environment.Version.Major == 9 ? "verified" : "mismatch", ".NET 9 x64",
                RuntimeInformation.FrameworkDescription,
                Note: "The running CLI proves this runtime is available; an absent .NET runtime prevents doctor from starting."),
        };
        checks.Add(CheckVersionedFile("viewerExecutable", viewerPath, "2026.2",
            info => info.ProductVersion == "2026.2",
            info => info.ProductVersion,
            "File metadata only; exact decoder build and SKU require a report operation."));
        // A filesystem root is not a valid Viewer executable, but doctor should
        // report it as a missing dependency instead of throwing while deriving
        // the adjacent bridge/Qt checks.
        var viewerDirectory = Path.GetDirectoryName(viewerPath) ?? viewerPath;
        var bridgePath = Path.Combine(viewerDirectory, "Plugins", "generic", "solidprobe.dll");
        checks.Add(new("viewerBridge", File.Exists(bridgePath) ? "present" : "missing",
            ViewerProbeRunner.ExpectedBridgeVersion, File.Exists(bridgePath) ? "file present" : "file absent",
            bridgePath, "Presence does not verify the loaded bridge version; runtime confirmation is still required."));
        checks.Add(CheckVersionedFile("viewerQt", Path.Combine(viewerDirectory, "Qt6Core.dll"),
            ViewerProbeRunner.ExpectedQtVersion,
            info => info.FileMajorPart == 6 && info.FileMinorPart == 8 && info.FileBuildPart == 1,
            info => info.FileVersion,
            "File metadata only; the loaded Qt version is confirmed by a report operation."));
        checks.Add(CheckDirectory("runRoot", runRoot));
        checks.Add(CheckDirectory("sessionRoot", sessionRoot));
        checks.Add(new("viewerRuntime", "notChecked",
            $"version={ViewerProbeRunner.ExpectedProductVersion}; build={ViewerProbeRunner.ExpectedProductBuild}; " +
            $"sku={ViewerProbeRunner.ExpectedProductSku}; qt={ViewerProbeRunner.ExpectedQtVersion}; bridge={ViewerProbeRunner.ExpectedBridgeVersion}",
            Note: "doctor never launches Viewer. A successful report operation must validate the decoder, bridge, report identity and scope."));
        return OperationResult.Success(new DoctorValue(
            VersionOperation.Describe(),
            checks.All(item => item.State is "verified" or "present" or "notCreated" or "notChecked"),
            "notChecked", runRoot, sessionRoot, checks, "references/setup.md"));
    }

    private static DependencyCheck CheckVersionedFile(
        string component, string path, string expected,
        Func<FileVersionInfo, bool> matches, Func<FileVersionInfo, string?> observed, string note)
    {
        if (!File.Exists(path)) return new(component, "missing", expected, "file absent", path, note);
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return new(component, matches(info) ? "present" : "mismatch", expected, observed(info), path, note);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new(component, "unreadable", expected, exception.GetType().Name, path, note);
        }
    }

    private static DependencyCheck CheckDirectory(string component, string path)
    {
        if (Directory.Exists(path))
            return new(component, "present", "local directory", "exists", path, "Write permission is not tested by this read-only check.");
        for (var ancestor = path; ancestor is not null; ancestor = Path.GetDirectoryName(ancestor))
        {
            if (File.Exists(ancestor))
                return new(component, "notDirectory", "local directory", $"file blocks directory: {ancestor}", path);
        }
        return new(component, "notCreated", "local directory", "created when needed by a report operation", path,
            "No directory was created and write permission was not tested.");
    }
}
