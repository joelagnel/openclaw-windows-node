namespace OpenClaw.SetupEngine;

internal sealed record LocalAiSetupPaths(
    string RootDirectory,
    string DownloadsDirectory,
    string EnginesDirectory,
    string StagingDirectory,
    string EngineDirectory,
    string EngineExecutablePath,
    string ModelsDirectory,
    string LogsDirectory,
    string ArchivePath);

/// <summary>
/// Resolves setup-owned Local AI paths and guards every recursive-operation
/// target against traversal and reparse-point redirection.
/// </summary>
internal static class LocalAiPathPolicy
{
    internal const string RootDirectoryName = "LocalAI";
    private const StringComparison PathComparison = StringComparison.OrdinalIgnoreCase;

    public static bool TryResolve(
        string localDataDir,
        OllamaReleaseArtifact artifact,
        out LocalAiSetupPaths paths,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        paths = null!;

        if (string.IsNullOrWhiteSpace(localDataDir))
        {
            error = "Local AI data directory is required.";
            return false;
        }

        if (!IsSafePathSegment(artifact.Version) ||
            !IsSafePathSegment(artifact.RuntimeIdentifier) ||
            !IsSafePathSegment(artifact.FileName))
        {
            error = "Ollama artifact metadata contains an invalid path segment.";
            return false;
        }

        string localDataRoot;
        string root;
        string downloads;
        string engines;
        string staging;
        string engineDirectory;
        string models;
        string logs;
        string archive;
        try
        {
            localDataRoot = NormalizePath(localDataDir);
            root = NormalizePath(Path.Combine(localDataRoot, RootDirectoryName));
            downloads = NormalizePath(Path.Combine(root, "downloads"));
            engines = NormalizePath(Path.Combine(root, "engines"));
            staging = NormalizePath(Path.Combine(root, "staging"));
            engineDirectory = NormalizePath(Path.Combine(
                engines,
                LocalAiConfig.DefaultEngine,
                artifact.Version,
                artifact.RuntimeIdentifier));
            models = NormalizePath(Path.Combine(root, "models"));
            logs = NormalizePath(Path.Combine(root, "logs"));
            archive = NormalizePath(Path.Combine(downloads, artifact.FileName));
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

        foreach (var candidate in new[] { root, downloads, engines, staging, engineDirectory, models, logs, archive })
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
            engineDirectory,
            Path.Combine(engineDirectory, "ollama.exe"),
            models,
            logs,
            archive);
        error = "";
        return true;
    }

    public static bool TryGetStagingDirectory(
        LocalAiSetupPaths paths,
        string runId,
        out string stagingDirectory,
        out string error)
    {
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

        if (!IsStrictDescendant(stagingDirectory, paths.RootDirectory))
        {
            stagingDirectory = "";
            error = "Local AI staging directory escaped the app-owned Local AI root.";
            return false;
        }

        error = "";
        return true;
    }

    public static bool TryValidateManagedDeleteTarget(
        string localDataDir,
        string candidatePath,
        out string deletePath,
        out string error)
    {
        deletePath = "";
        if (string.IsNullOrWhiteSpace(localDataDir))
        {
            error = "Local AI data directory is required.";
            return false;
        }

        string localDataRoot;
        string root;
        try
        {
            localDataRoot = NormalizePath(localDataDir);
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
            error = "Ollama archive contains an empty or invalid entry name.";
            return false;
        }

        string root;
        try
        {
            root = NormalizePath(stagingDirectory);
            destinationPath = NormalizePath(Path.Combine(root, entryName.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"Ollama archive entry has an invalid path: {ex.Message}";
            return false;
        }

        if (!IsStrictDescendant(destinationPath, root))
        {
            destinationPath = "";
            error = $"Ollama archive entry '{entryName}' escapes its staging directory.";
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
        string localDataRoot,
        string candidatePath,
        out string error)
    {
        if (!IsSameOrDescendant(candidatePath, localDataRoot))
        {
            error = $"Local AI path '{candidatePath}' is not contained within '{localDataRoot}'.";
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

            if (PathEquals(current, localDataRoot))
            {
                error = "";
                return true;
            }

            current = Path.GetDirectoryName(current);
        }

        error = $"Local AI path '{candidatePath}' is not contained within '{localDataRoot}'.";
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

    private static bool IsSafePathSegment(string value)
        => !string.IsNullOrWhiteSpace(value) &&
           string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
           value is not "." and not ".." &&
           !value.EndsWith('.') &&
           !Path.IsPathRooted(value) &&
           value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
           !value.Contains(Path.DirectorySeparatorChar) &&
           !value.Contains(Path.AltDirectorySeparatorChar);

    private static bool IsSameOrDescendant(string candidate, string root)
        => PathEquals(candidate, root) || IsStrictDescendant(candidate, root);

    private static bool IsStrictDescendant(string candidate, string root)
        => candidate.StartsWith(root + Path.DirectorySeparatorChar, PathComparison);

    private static bool PathEquals(string? left, string right)
        => string.Equals(left, right, PathComparison);
}
