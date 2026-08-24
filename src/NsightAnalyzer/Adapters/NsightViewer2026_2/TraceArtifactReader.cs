using System.Globalization;
using System.Security.Cryptography;
using NsightAnalyzer.Contracts;

namespace NsightAnalyzer.Adapters.NsightViewer2026_2;

internal sealed record TraceArtifact(
    string FullPath,
    string FileName,
    long ByteLength,
    DateTime LastWriteTimeUtc,
    string IdentityMode,
    string ArtifactIdentity,
    string? Sha256,
    string ReportId)
{
    public TraceArtifactInfo ToContract() =>
        new(FileName, ByteLength, LastWriteTimeUtc, IdentityMode, ArtifactIdentity, Sha256);
}

internal sealed record TraceArtifactOpenResult(
    TraceArtifact? Artifact,
    OperationError? Error)
{
    public bool IsSuccess => Artifact is not null;
}

internal static class TraceArtifactReader
{
    public static async Task<TraceArtifactOpenResult> OpenAsync(
        string tracePath,
        string identityMode,
        int timeoutMs)
    {
        if (string.IsNullOrWhiteSpace(tracePath))
        {
            return Fail(
                ErrorCategory.InvalidInput,
                "trace.path_required",
                "A .ngfx-gputrace path is required.");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(tracePath);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Fail(
                ErrorCategory.InvalidInput,
                "trace.path_invalid",
                "The GPU Trace path is invalid.",
                exception.GetType().Name);
        }

        if (!Path.GetExtension(fullPath).Equals(
                ".ngfx-gputrace", StringComparison.OrdinalIgnoreCase))
        {
            return Fail(
                ErrorCategory.InvalidInput,
                "trace.extension_invalid",
                "The input must have the .ngfx-gputrace extension.");
        }

        FileSnapshot before;
        try
        {
            before = Snapshot(fullPath);
        }
        catch (FileNotFoundException)
        {
            return Fail(
                ErrorCategory.NotFound,
                "trace.not_found",
                "The GPU Trace file was not found.");
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException)
        {
            return Fail(
                ErrorCategory.Unavailable,
                "trace.file_unreadable",
                "The GPU Trace file metadata could not be read.",
                exception.GetType().Name);
        }

        if (before.ByteLength == 0)
        {
            return Fail(
                ErrorCategory.InvalidInput,
                "trace.file_empty",
                "The GPU Trace file is empty.");
        }

        string? sha256 = null;
        var artifactIdentity = string.Create(
            CultureInfo.InvariantCulture,
            $"localWeak:length={before.ByteLength}:mtimeUtcTicks={before.LastWriteTimeUtc.Ticks}");
        if (identityMode.Equals("sha256", StringComparison.Ordinal))
        {
            try
            {
                using var cancellation = new CancellationTokenSource(timeoutMs);
                await using var stream = new FileStream(
                    fullPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    1024 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                sha256 = Convert.ToHexString(
                    await SHA256.HashDataAsync(stream, cancellation.Token));
            }
            catch (OperationCanceledException)
            {
                return Fail(
                    ErrorCategory.Timeout,
                    "trace.hash_timeout",
                    "Computing the GPU Trace SHA-256 exceeded the timeout.",
                    $"timeoutMs={timeoutMs}");
            }
            catch (Exception exception) when (
                exception is FileNotFoundException or UnauthorizedAccessException or IOException)
            {
                return Fail(
                    ErrorCategory.Unavailable,
                    "trace.file_unreadable",
                    "The GPU Trace file could not be read for hashing.",
                    exception.GetType().Name);
            }

            FileSnapshot after;
            try
            {
                after = Snapshot(fullPath);
            }
            catch (Exception exception) when (
                exception is FileNotFoundException or UnauthorizedAccessException or IOException)
            {
                return Fail(
                    ErrorCategory.Unavailable,
                    "trace.file_changed",
                    "The GPU Trace file could not be revalidated after hashing.",
                    exception.GetType().Name);
            }
            if (before != after)
            {
                return Fail(
                    ErrorCategory.Unavailable,
                    "trace.file_changed",
                    "The GPU Trace file changed while it was being hashed.");
            }
            artifactIdentity = $"sha256:{sha256}";
        }

        var fileName = Path.GetFileName(fullPath);
        var reportId = string.Create(
            CultureInfo.InvariantCulture,
            $"{fileName}:{before.ByteLength}:{before.LastWriteTimeUtc.Ticks}");
        return new(
            new(
                fullPath,
                fileName,
                before.ByteLength,
                before.LastWriteTimeUtc,
                identityMode,
                artifactIdentity,
                sha256,
                reportId),
            null);
    }

    private static FileSnapshot Snapshot(string fullPath)
    {
        var item = new FileInfo(fullPath);
        if (!item.Exists)
        {
            throw new FileNotFoundException("Trace file not found.", fullPath);
        }
        return new(item.Length, item.LastWriteTimeUtc);
    }

    private static TraceArtifactOpenResult Fail(
        ErrorCategory category,
        string code,
        string message,
        string? detail = null) =>
        new(null, new(category, code, message, detail));

    private sealed record FileSnapshot(long ByteLength, DateTime LastWriteTimeUtc);
}
