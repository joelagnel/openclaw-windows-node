namespace OpenClaw.SetupEngine;

/// <summary>
/// Identifies one versioned native component without prescribing its vendor,
/// download source, archive contents, or executable layout.
/// </summary>
internal sealed record LocalAiComponentIdentity(
    string Name,
    string Version,
    string RuntimeIdentifier);

internal sealed record LocalAiSetupPaths(
    string RootDirectory,
    string DownloadsDirectory,
    string EnginesDirectory,
    string StagingDirectory,
    string InstallDirectory,
    string ModelsDirectory,
    string LogsDirectory);

/// <summary>
/// Resolves setup-owned Local AI paths and guards every recursive-operation
/// target against traversal and reparse-point redirection.
/// </summary>
internal static class LocalAiPathPolicy
{
    internal const string RootDirectoryName = "LocalAI";
    private const StringComparison PathComparison = StringComparison.OrdinalIgnoreCase;

    public static bool TryResolve(
        string localDataDirectory,
        LocalAiComponentIdentity identity,
        out LocalAiSetupPaths paths,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(identity);
        paths = null!;

        if (string.IsNullOrWhiteSpace(localDataDirectory))
        {
            error = "Local AI data directory is required.";
            return false;
        }

        if (!IsSafeWindowsPathSegment(identity.Name) ||
            !IsSafeWindowsPathSegment(identity.Version) ||
            !IsSafeWindowsPathSegment(identity.RuntimeIdentifier))
        {
            error = "Local AI component identity contains an invalid path segment.";
            return false;
        }

        string localDataRoot;
        string root;
        string downloads;
        string engines;
        string staging;
        string installDirectory;
        string models;
        string logs;
        try
        {
            localDataRoot = NormalizePath(localDataDirectory);
            root = NormalizePath(Path.Combine(localDataRoot, RootDirectoryName));
            downloads = NormalizePath(Path.Combine(root, "downloads"));
            engines = NormalizePath(Path.Combine(root, "engines"));
            staging = NormalizePath(Path.Combine(root, "staging"));
            installDirectory = NormalizePath(Path.Combine(
                engines,
                identity.Name,
                identity.Version,
                identity.RuntimeIdentifier));
            models = NormalizePath(Path.Combine(root, "models"));
            logs = NormalizePath(Path.Combine(root, "logs"));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"Invalid Local AI data path: {ex.Message}";
            return false;
        }

        if (!PathEquals(Path.GetDirectoryName(root), localDataRoot))
        {
            error = $"Local AI root '{root}' must be an immediate child of '{localDataRoot}'.";
            return false;
        }

        foreach (var candidate in new[]
                 {
                     root,
                     downloads,
                     engines,
                     staging,
                     installDirectory,
                     models,
                     logs,
                 })
        {
            if (!IsSameOrDescendant(candidate, root))
            {
                error = $"Local AI path '{candidate}' escaped the app-owned Local AI root.";
                return false;
            }

            if (!TryValidateExistingPathChain(localDataRoot, candidate, out error))
                return false;
        }

        paths = new LocalAiSetupPaths(
            root,
            downloads,
            engines,
            staging,
            installDirectory,
            models,
            logs);
        error = "";
        return true;
    }

    public static bool TryGetDownloadPath(
        LocalAiSetupPaths paths,
        string archiveFileName,
        out string downloadPath,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(paths);
        downloadPath = "";

        if (!IsSafeWindowsPathSegment(archiveFileName))
        {
            error = "Local AI archive file name contains an invalid path segment.";
            return false;
        }

        try
        {
            downloadPath = NormalizePath(Path.Combine(paths.DownloadsDirectory, archiveFileName));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"Invalid Local AI download path: {ex.Message}";
            return false;
        }

        if (!IsStrictDescendant(downloadPath, paths.DownloadsDirectory) ||
            !IsStrictDescendant(downloadPath, paths.RootDirectory))
        {
            downloadPath = "";
            error = "Local AI download path escaped the app-owned Local AI root.";
            return false;
        }

        if (!TryValidateExistingPathChain(paths.RootDirectory, downloadPath, out error))
        {
            downloadPath = "";
            return false;
        }

        error = "";
        return true;
    }

    public static bool TryGetStagingDirectory(
        LocalAiSetupPaths paths,
        string runId,
        out string stagingDirectory,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(paths);
        stagingDirectory = "";
        if (string.IsNullOrWhiteSpace(runId) ||
            runId.Length is < 8 or > 64 ||
            !runId.All(char.IsAsciiHexDigit))
        {
            error = "Local AI staging run ID must contain 8 to 64 ASCII hexadecimal characters.";
            return false;
        }

        try
        {
            stagingDirectory = NormalizePath(Path.Combine(paths.StagingDirectory, runId));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"Invalid Local AI staging path: {ex.Message}";
            return false;
        }

        if (!IsStrictDescendant(stagingDirectory, paths.StagingDirectory) ||
            !IsStrictDescendant(stagingDirectory, paths.RootDirectory))
        {
            stagingDirectory = "";
            error = "Local AI staging directory escaped the app-owned Local AI root.";
            return false;
        }

        if (!TryValidateExistingPathChain(paths.RootDirectory, stagingDirectory, out error))
        {
            stagingDirectory = "";
            return false;
        }

        error = "";
        return true;
    }

    public static bool TryValidateManagedDeleteTarget(
        string localDataDirectory,
        string candidatePath,
        out string deletePath,
        out string error)
    {
        deletePath = "";
        if (string.IsNullOrWhiteSpace(localDataDirectory))
        {
            error = "Local AI data directory is required.";
            return false;
        }

        string localDataRoot;
        string root;
        try
        {
            localDataRoot = NormalizePath(localDataDirectory);
            root = NormalizePath(Path.Combine(localDataRoot, RootDirectoryName));
            deletePath = NormalizePath(candidatePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"Invalid Local AI deletion path: {ex.Message}";
            return false;
        }

        if (!IsStrictDescendant(deletePath, root))
        {
            error = $"Refusing to delete Local AI path '{deletePath}'; it is not below the app-owned root '{root}'.";
            deletePath = "";
            return false;
        }

        if (!TryValidateExistingPathChain(localDataRoot, deletePath, out error))
        {
            deletePath = "";
            return false;
        }

        return true;
    }

    public static bool TryResolveArchiveEntryDestination(
        string stagingDirectory,
        string entryName,
        out string destinationPath,
        out string error)
    {
        destinationPath = "";
        if (string.IsNullOrWhiteSpace(entryName) || entryName.IndexOf('\0') >= 0)
        {
            error = "Local AI archive contains an empty or invalid entry name.";
            return false;
        }

        string root;
        try
        {
            root = NormalizePath(stagingDirectory);
            destinationPath = NormalizePath(Path.Combine(
                root,
                entryName
                    .Replace('/', Path.DirectorySeparatorChar)
                    .Replace('\\', Path.DirectorySeparatorChar)));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"Local AI archive entry has an invalid path: {ex.Message}";
            return false;
        }

        if (!IsStrictDescendant(destinationPath, root))
        {
            destinationPath = "";
            error = $"Local AI archive entry '{entryName}' escapes its staging directory.";
            return false;
        }

        if (!TryValidateExistingPathChain(root, destinationPath, out error))
        {
            destinationPath = "";
            return false;
        }

        error = "";
        return true;
    }

    private static bool TryValidateExistingPathChain(
        string containmentRoot,
        string candidatePath,
        out string error)
    {
        if (!IsSameOrDescendant(candidatePath, containmentRoot))
        {
            error = $"Local AI path '{candidatePath}' is not contained within '{containmentRoot}'.";
            return false;
        }

        string? current = candidatePath;
        while (current is not null)
        {
            if (!TryGetExistingAttributes(current, out var exists, out var attributes, out error))
                return false;
            if (exists && attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                error = $"Refusing to operate under '{current}' because it is a reparse point.";
                return false;
            }

            if (PathEquals(current, containmentRoot))
            {
                error = "";
                return true;
            }

            current = Path.GetDirectoryName(current);
        }

        error = $"Local AI path '{candidatePath}' is not contained within '{containmentRoot}'.";
        return false;
    }

    private static bool TryGetExistingAttributes(
        string path,
        out bool exists,
        out FileAttributes attributes,
        out string error)
    {
        try
        {
            attributes = File.GetAttributes(path);
            exists = true;
            error = "";
            return true;
        }
        catch (FileNotFoundException)
        {
            attributes = default;
            exists = false;
            error = "";
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            attributes = default;
            exists = false;
            error = "";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            attributes = default;
            exists = false;
            error = $"Cannot verify Local AI path '{path}': {ex.Message}";
            return false;
        }
    }

    private static string NormalizePath(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool IsSafeWindowsPathSegment(string value)
        => !string.IsNullOrWhiteSpace(value) &&
           string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
           value is not "." and not ".." &&
           !value.EndsWith('.') &&
           !Path.IsPathRooted(value) &&
           value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
           !value.Contains(Path.DirectorySeparatorChar) &&
           !value.Contains(Path.AltDirectorySeparatorChar) &&
           !IsWindowsDeviceName(value);

    internal static bool IsWindowsDeviceName(string segment)
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

    private static bool IsSameOrDescendant(string candidate, string root)
        => PathEquals(candidate, root) || IsStrictDescendant(candidate, root);

    private static bool IsStrictDescendant(string candidate, string root)
        => candidate.StartsWith(EnsureTrailingDirectorySeparator(root), PathComparison);

    private static string EnsureTrailingDirectorySeparator(string path)
        => Path.EndsInDirectorySeparator(path) ? path : path + Path.DirectorySeparatorChar;

    private static bool PathEquals(string? left, string right)
        => string.Equals(left, right, PathComparison);
}
