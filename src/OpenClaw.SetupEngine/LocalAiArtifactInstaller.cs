using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace OpenClaw.SetupEngine;

internal enum LocalAiArtifactInstallPhase
{
    Downloading,
    Verifying,
    Extracting,
    Promoting,
    Complete,
}

internal enum LocalAiArtifactProgressUnit
{
    None,
    Bytes,
    Entries,
}

internal sealed record LocalAiArtifactInstallProgress(
    LocalAiArtifactInstallPhase Phase,
    long Completed,
    long? Total,
    LocalAiArtifactProgressUnit Unit)
{
    public double? Fraction => Total is > 0
        ? Math.Clamp((double)Completed / Total.Value, 0, 1)
        : null;
}

/// <summary>
/// Describes the one directory a transaction may remove to roll back this install.
/// The archive and staging paths are always cleaned before this result is returned.
/// </summary>
internal sealed record LocalAiArtifactInstallResult(
    string Version,
    string RuntimeIdentifier,
    string EngineDirectory,
    string EngineExecutablePath,
    string ModelsDirectory,
    long VerifiedArchiveSizeBytes,
    string VerifiedArchiveSha256,
    bool CreatedEngineDirectory)
{
    public string? RollbackDirectory => CreatedEngineDirectory ? EngineDirectory : null;
}

internal sealed class LocalAiArtifactInstallException : Exception
{
    public LocalAiArtifactInstallException(string message)
        : base(message)
    {
    }

    public LocalAiArtifactInstallException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal interface ILocalAiArtifactInstaller
{
    Task<LocalAiArtifactInstallResult> InstallAsync(
        string localDataDirectory,
        Architecture architecture,
        IProgress<LocalAiArtifactInstallProgress>? progress,
        CancellationToken cancellationToken);
}

/// <summary>
/// Acquires a policy-pinned Ollama archive, verifies it, safely extracts it into
/// a disposable staging directory, then atomically promotes the directory.
/// </summary>
internal sealed class LocalAiArtifactInstaller : ILocalAiArtifactInstaller
{
    private const int DownloadBufferSize = 128 * 1024;
    private const int DownloadProgressIntervalBytes = 4 * 1024 * 1024;
    private const int UnixFileTypeMask = 0xF000;
    private const int UnixRegularFile = 0x8000;
    private const int UnixDirectory = 0x4000;
    private const int UnixSymbolicLink = 0xA000;

    private readonly HttpClient _httpClient;

    public LocalAiArtifactInstaller(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public event EventHandler<LocalAiArtifactInstallProgress>? ProgressChanged;

    public Task<LocalAiArtifactInstallResult> InstallAsync(
        string localDataDirectory,
        Architecture architecture,
        IProgress<LocalAiArtifactInstallProgress>? progress,
        CancellationToken cancellationToken)
        => InstallAsync(
            localDataDirectory,
            OllamaReleasePolicy.Resolve(architecture),
            progress,
            cancellationToken);

    internal async Task<LocalAiArtifactInstallResult> InstallAsync(
        string localDataDirectory,
        OllamaReleaseArtifact artifact,
        IProgress<LocalAiArtifactInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ValidateArtifact(artifact);

        if (!LocalAiPathPolicy.TryResolve(localDataDirectory, artifact, out var paths, out var pathError))
            throw new LocalAiArtifactInstallException(pathError);

        var runId = Guid.NewGuid().ToString("N");
        if (!LocalAiPathPolicy.TryGetStagingDirectory(paths, runId, out var stagingDirectory, out pathError))
            throw new LocalAiArtifactInstallException(pathError);

        var partialArchivePath = paths.ArchivePath + ".partial";
        var stagingCreated = false;
        var promoted = false;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsurePromotionTargetDoesNotExist(paths.EngineDirectory);

            Directory.CreateDirectory(paths.DownloadsDirectory);
            Directory.CreateDirectory(paths.StagingDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(paths.EngineDirectory)!);

            RevalidatePaths(localDataDirectory, artifact, paths);
            RemoveStalePartial(localDataDirectory, partialArchivePath);

            if (Directory.Exists(stagingDirectory) || File.Exists(stagingDirectory))
                throw new LocalAiArtifactInstallException("The Local AI staging run directory already exists.");

            Directory.CreateDirectory(stagingDirectory);
            stagingCreated = true;

            var verifiedHash = await DownloadAndVerifyAsync(
                artifact,
                partialArchivePath,
                progress,
                cancellationToken).ConfigureAwait(false);

            await ExtractArchiveAsync(
                partialArchivePath,
                stagingDirectory,
                progress,
                cancellationToken).ConfigureAwait(false);

            ValidateStagedExecutable(stagingDirectory);
            cancellationToken.ThrowIfCancellationRequested();

            RevalidatePaths(localDataDirectory, artifact, paths);
            EnsurePromotionTargetDoesNotExist(paths.EngineDirectory);
            Report(progress, new(
                LocalAiArtifactInstallPhase.Promoting,
                0,
                1,
                LocalAiArtifactProgressUnit.None));

            Directory.Move(stagingDirectory, paths.EngineDirectory);
            promoted = true;

            var result = new LocalAiArtifactInstallResult(
                artifact.Version,
                artifact.RuntimeIdentifier,
                paths.EngineDirectory,
                paths.EngineExecutablePath,
                paths.ModelsDirectory,
                artifact.SizeBytes,
                verifiedHash,
                CreatedEngineDirectory: true);

            Report(progress, new(
                LocalAiArtifactInstallPhase.Complete,
                1,
                1,
                LocalAiArtifactProgressUnit.None));
            return result;
        }
        finally
        {
            TryDeleteManagedFile(localDataDirectory, partialArchivePath);
            if (stagingCreated && !promoted)
                TryDeleteManagedDirectory(localDataDirectory, stagingDirectory);
        }
    }

    private async Task<string> DownloadAndVerifyAsync(
        OllamaReleaseArtifact artifact,
        string partialArchivePath,
        IProgress<LocalAiArtifactInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, artifact.DownloadUri);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new LocalAiArtifactInstallException(
                $"Ollama download failed with HTTP status {(int)response.StatusCode} ({response.StatusCode}).");
        }

        if (response.Content.Headers.ContentLength is { } contentLength && contentLength != artifact.SizeBytes)
        {
            throw new LocalAiArtifactInstallException(
                $"Ollama download declared {contentLength} bytes; expected {artifact.SizeBytes} bytes.");
        }

        Report(progress, new(
            LocalAiArtifactInstallPhase.Downloading,
            0,
            artifact.SizeBytes,
            LocalAiArtifactProgressUnit.Bytes));

        long downloaded = 0;
        long lastReportedDownloadBytes = 0;
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var destination = new FileStream(
            partialArchivePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            DownloadBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var buffer = new byte[DownloadBufferSize];
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;

                downloaded = checked(downloaded + read);
                if (downloaded > artifact.SizeBytes)
                {
                    throw new LocalAiArtifactInstallException(
                        $"Ollama download exceeded its expected size of {artifact.SizeBytes} bytes.");
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                hasher.AppendData(buffer, 0, read);
                if (downloaded == artifact.SizeBytes ||
                    downloaded - lastReportedDownloadBytes >= DownloadProgressIntervalBytes)
                {
                    Report(progress, new(
                        LocalAiArtifactInstallPhase.Downloading,
                        downloaded,
                        artifact.SizeBytes,
                        LocalAiArtifactProgressUnit.Bytes));
                    lastReportedDownloadBytes = downloaded;
                }
            }

            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        if (downloaded != artifact.SizeBytes)
        {
            throw new LocalAiArtifactInstallException(
                $"Ollama download contained {downloaded} bytes; expected {artifact.SizeBytes} bytes.");
        }

        Report(progress, new(
            LocalAiArtifactInstallPhase.Verifying,
            downloaded,
            artifact.SizeBytes,
            LocalAiArtifactProgressUnit.Bytes));

        var actualHashBytes = hasher.GetHashAndReset();
        var expectedHashBytes = Convert.FromHexString(artifact.Sha256);
        if (!CryptographicOperations.FixedTimeEquals(actualHashBytes, expectedHashBytes))
            throw new LocalAiArtifactInstallException("Ollama download failed SHA-256 verification.");

        return Convert.ToHexString(actualHashBytes).ToLowerInvariant();
    }

    private async Task ExtractArchiveAsync(
        string archivePath,
        string stagingDirectory,
        IProgress<LocalAiArtifactInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var archiveStream = new FileStream(
                archivePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                DownloadBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: false);
            var totalEntries = archive.Entries.Count;
            long completedEntries = 0;

            Report(progress, new(
                LocalAiArtifactInstallPhase.Extracting,
                completedEntries,
                totalEntries,
                LocalAiArtifactProgressUnit.Entries));

            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidateArchiveEntryName(entry.FullName);
                ValidateArchiveEntryType(entry);

                if (!LocalAiPathPolicy.TryResolveArchiveEntryDestination(
                        stagingDirectory,
                        entry.FullName,
                        out var destinationPath,
                        out var pathError))
                {
                    throw new LocalAiArtifactInstallException(pathError);
                }

                var isDirectory = entry.Name.Length == 0;
                if (isDirectory)
                {
                    Directory.CreateDirectory(destinationPath);
                }
                else
                {
                    var parentDirectory = Path.GetDirectoryName(destinationPath)
                        ?? throw new LocalAiArtifactInstallException("Ollama archive entry has no parent directory.");
                    Directory.CreateDirectory(parentDirectory);

                    if (!LocalAiPathPolicy.TryResolveArchiveEntryDestination(
                            stagingDirectory,
                            entry.FullName,
                            out destinationPath,
                            out pathError))
                    {
                        throw new LocalAiArtifactInstallException(pathError);
                    }

                    await using var source = entry.Open();
                    await using var destination = new FileStream(
                        destinationPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        DownloadBufferSize,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await source.CopyToAsync(destination, DownloadBufferSize, cancellationToken).ConfigureAwait(false);
                }

                completedEntries++;
                Report(progress, new(
                    LocalAiArtifactInstallPhase.Extracting,
                    completedEntries,
                    totalEntries,
                    LocalAiArtifactProgressUnit.Entries));
            }
        }
        catch (InvalidDataException ex)
        {
            throw new LocalAiArtifactInstallException("Ollama download is not a valid ZIP archive.", ex);
        }
    }

    private static void ValidateArchiveEntryType(ZipArchiveEntry entry)
    {
        var windowsAttributes = (FileAttributes)(entry.ExternalAttributes & 0xFFFF);
        if (windowsAttributes.HasFlag(FileAttributes.ReparsePoint))
            throw new LocalAiArtifactInstallException($"Ollama archive entry '{entry.FullName}' is a reparse point.");

        var unixMode = (entry.ExternalAttributes >> 16) & 0xFFFF;
        var unixFileType = unixMode & UnixFileTypeMask;
        if (unixFileType == UnixSymbolicLink)
            throw new LocalAiArtifactInstallException($"Ollama archive entry '{entry.FullName}' is a symbolic link.");
        if (unixFileType is not 0 and not UnixRegularFile and not UnixDirectory)
            throw new LocalAiArtifactInstallException($"Ollama archive entry '{entry.FullName}' has an unsupported file type.");
    }

    private static void ValidateArchiveEntryName(string entryName)
    {
        if (string.IsNullOrWhiteSpace(entryName) || entryName.IndexOf('\0') >= 0)
            throw new LocalAiArtifactInstallException("Ollama archive contains an empty or invalid entry name.");

        var normalized = entryName.Replace('\\', '/');
        var segments = normalized.Split('/');
        for (var index = 0; index < segments.Length; index++)
        {
            var segment = segments[index];
            var isTrailingDirectoryMarker = index == segments.Length - 1 && segment.Length == 0;
            if (isTrailingDirectoryMarker)
                continue;

            if (string.IsNullOrWhiteSpace(segment) ||
                segment is "." or ".." ||
                !string.Equals(segment, segment.Trim(), StringComparison.Ordinal) ||
                segment.EndsWith('.') ||
                segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                IsWindowsDeviceName(segment))
            {
                throw new LocalAiArtifactInstallException(
                    $"Ollama archive entry '{entryName}' contains an unsafe path segment.");
            }
        }
    }

    private static bool IsWindowsDeviceName(string segment)
    {
        var baseName = segment.Split('.')[0];
        return baseName.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
               baseName.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
               baseName.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
               baseName.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
               IsNumberedDevice(baseName, "COM") ||
               IsNumberedDevice(baseName, "LPT");
    }

    private static bool IsNumberedDevice(string value, string prefix)
        => value.Length == 4 &&
           value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
           value[3] is >= '1' and <= '9';

    private static void ValidateStagedExecutable(string stagingDirectory)
    {
        if (!LocalAiPathPolicy.TryResolveArchiveEntryDestination(
                stagingDirectory,
                "ollama.exe",
                out var executablePath,
                out var pathError))
        {
            throw new LocalAiArtifactInstallException(pathError);
        }

        if (!File.Exists(executablePath))
            throw new LocalAiArtifactInstallException("Ollama archive does not contain ollama.exe at its root.");

        var attributes = File.GetAttributes(executablePath);
        if (attributes.HasFlag(FileAttributes.Directory) || attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new LocalAiArtifactInstallException("Extracted ollama.exe is not a regular file.");
        if (new FileInfo(executablePath).Length == 0)
            throw new LocalAiArtifactInstallException("Extracted ollama.exe is empty.");
    }

    private static void ValidateArtifact(OllamaReleaseArtifact artifact)
    {
        if (artifact.SizeBytes <= 0)
            throw new ArgumentException("Ollama artifact expected size must be positive.", nameof(artifact));
        if (!artifact.DownloadUri.IsAbsoluteUri ||
            !string.Equals(artifact.DownloadUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Ollama artifact download URI must use HTTPS.", nameof(artifact));
        }

        try
        {
            if (Convert.FromHexString(artifact.Sha256).Length != 32 ||
                !string.Equals(artifact.Sha256, artifact.Sha256.ToLowerInvariant(), StringComparison.Ordinal))
            {
                throw new ArgumentException("Ollama artifact SHA-256 must be 64 lowercase hexadecimal characters.", nameof(artifact));
            }
        }
        catch (FormatException ex)
        {
            throw new ArgumentException(
                "Ollama artifact SHA-256 must be 64 lowercase hexadecimal characters.",
                nameof(artifact),
                ex);
        }
    }

    private static void RevalidatePaths(
        string localDataDirectory,
        OllamaReleaseArtifact artifact,
        LocalAiSetupPaths expectedPaths)
    {
        if (!LocalAiPathPolicy.TryResolve(localDataDirectory, artifact, out var currentPaths, out var pathError))
            throw new LocalAiArtifactInstallException(pathError);
        if (!string.Equals(currentPaths.EngineDirectory, expectedPaths.EngineDirectory, StringComparison.OrdinalIgnoreCase))
            throw new LocalAiArtifactInstallException("The Local AI engine path changed during installation.");
    }

    private static void EnsurePromotionTargetDoesNotExist(string engineDirectory)
    {
        if (Directory.Exists(engineDirectory) || File.Exists(engineDirectory))
        {
            throw new LocalAiArtifactInstallException(
                $"Refusing to replace existing Ollama engine path '{engineDirectory}'.");
        }
    }

    private static void RemoveStalePartial(string localDataDirectory, string partialArchivePath)
    {
        if (Directory.Exists(partialArchivePath))
            throw new LocalAiArtifactInstallException("The Ollama partial download path is an existing directory.");
        if (!File.Exists(partialArchivePath))
            return;
        if (!LocalAiPathPolicy.TryValidateManagedDeleteTarget(
                localDataDirectory,
                partialArchivePath,
                out var deletePath,
                out var pathError))
        {
            throw new LocalAiArtifactInstallException(pathError);
        }

        File.Delete(deletePath);
    }

    private static void TryDeleteManagedFile(string localDataDirectory, string path)
    {
        try
        {
            if (!File.Exists(path))
                return;
            if (LocalAiPathPolicy.TryValidateManagedDeleteTarget(
                    localDataDirectory,
                    path,
                    out var deletePath,
                    out _))
            {
                File.Delete(deletePath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            System.Diagnostics.Trace.TraceWarning("Could not clean Local AI partial download '{0}': {1}", path, ex.Message);
        }
    }

    private static void TryDeleteManagedDirectory(string localDataDirectory, string path)
    {
        try
        {
            if (!Directory.Exists(path))
                return;
            if (LocalAiPathPolicy.TryValidateManagedDeleteTarget(
                    localDataDirectory,
                    path,
                    out var deletePath,
                    out _))
            {
                Directory.Delete(deletePath, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            System.Diagnostics.Trace.TraceWarning("Could not clean Local AI staging directory '{0}': {1}", path, ex.Message);
        }
    }

    private void Report(
        IProgress<LocalAiArtifactInstallProgress>? progress,
        LocalAiArtifactInstallProgress value)
    {
        try
        {
            progress?.Report(value);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("Local AI progress observer failed: {0}", ex.Message);
        }

        try
        {
            ProgressChanged?.Invoke(this, value);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("Local AI progress event observer failed: {0}", ex.Message);
        }
    }
}
