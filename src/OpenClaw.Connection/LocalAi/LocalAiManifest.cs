using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenClaw.Connection.LocalAi;

/// <summary>Canonical, companion-owned locations for local inference artifacts.</summary>
public sealed class LocalAiPaths
{
    public LocalAiPaths(string localDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localDataDirectory);
        LocalDataDirectory = Path.GetFullPath(localDataDirectory);
        RootDirectory = Path.Combine(LocalDataDirectory, "LocalAI");
        ManifestPath = Path.Combine(RootDirectory, "state.json");
        EnginesDirectory = Path.Combine(RootDirectory, "engines");
        ModelsDirectory = Path.Combine(RootDirectory, "models");
        DownloadsDirectory = Path.Combine(RootDirectory, "downloads");
        StagingDirectory = Path.Combine(RootDirectory, "staging");
        LogsDirectory = Path.Combine(RootDirectory, "logs");
        StandardOutputLogPath = Path.Combine(LogsDirectory, "ollama.stdout.log");
        StandardErrorLogPath = Path.Combine(LogsDirectory, "ollama.stderr.log");
    }

    public string LocalDataDirectory { get; }
    public string RootDirectory { get; }
    public string ManifestPath { get; }
    public string EnginesDirectory { get; }
    public string ModelsDirectory { get; }
    public string DownloadsDirectory { get; }
    public string StagingDirectory { get; }
    public string LogsDirectory { get; }
    public string StandardOutputLogPath { get; }
    public string StandardErrorLogPath { get; }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(EnginesDirectory);
        Directory.CreateDirectory(ModelsDirectory);
        Directory.CreateDirectory(DownloadsDirectory);
        Directory.CreateDirectory(StagingDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }

    public string ResolveContainedPath(string relativePath, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new InvalidDataException($"{fieldName} must be a non-empty relative path.");
        if (Path.IsPathFullyQualified(relativePath) || Path.IsPathRooted(relativePath))
            throw new InvalidDataException($"{fieldName} must be relative to the local AI data directory.");

        var resolved = Path.GetFullPath(relativePath, RootDirectory);
        var rootWithSeparator = Path.TrimEndingDirectorySeparator(Path.GetFullPath(RootDirectory)) + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{fieldName} escapes the local AI data directory.");
        RejectExistingReparsePoints(resolved, fieldName);
        return resolved;
    }

    private void RejectExistingReparsePoints(string resolvedPath, string fieldName)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(RootDirectory));
        RejectIfReparsePoint(root, fieldName);
        var relative = Path.GetRelativePath(root, resolvedPath);
        var current = root;
        foreach (var segment in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current))
                break;
            RejectIfReparsePoint(current, fieldName);
        }
    }

    private static void RejectIfReparsePoint(string path, string fieldName)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            return;
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"{fieldName} contains an existing reparse point.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"{fieldName} could not be safely validated.", ex);
        }
    }
}

public sealed record LocalAiInstallManifest
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string Engine { get; init; } = "ollama";
    public required string EngineVersion { get; init; }
    public required string Architecture { get; init; }
    public required string ExecutablePath { get; init; }
    public required string ModelsPath { get; init; }
    public required string ModelTag { get; init; }
    public string Endpoint { get; init; } = "http://127.0.0.1:11434";
    public DateTimeOffset InstalledAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public int? ContextLength { get; init; }
}

public sealed record LocalAiResolvedInstall(
    LocalAiInstallManifest Manifest,
    string ExecutablePath,
    string ModelsPath,
    Uri Endpoint);

/// <summary>Persists the installation manifest with same-directory atomic replacement.</summary>
public sealed class LocalAiManifestStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly LocalAiPaths _paths;

    public LocalAiManifestStore(LocalAiPaths paths) => _paths = paths ?? throw new ArgumentNullException(nameof(paths));

    public async Task<LocalAiResolvedInstall?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_paths.ManifestPath))
            return null;

        LocalAiInstallManifest? manifest;
        try
        {
            await using var stream = new FileStream(
                _paths.ManifestPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            manifest = await JsonSerializer.DeserializeAsync<LocalAiInstallManifest>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The local AI installation manifest is invalid JSON.", ex);
        }

        return ResolveAndValidate(manifest ?? throw new InvalidDataException("The local AI installation manifest is empty."));
    }

    public async Task SaveAsync(LocalAiInstallManifest manifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        _ = ResolveAndValidate(manifest);
        Directory.CreateDirectory(_paths.RootDirectory);
        var temporaryPath = Path.Combine(_paths.RootDirectory, $".{Path.GetFileName(_paths.ManifestPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, manifest, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _paths.ManifestPath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporaryPath); } catch { }
        }
    }

    public Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(_paths.ManifestPath);
        return Task.CompletedTask;
    }

    public LocalAiResolvedInstall ResolveAndValidate(LocalAiInstallManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.SchemaVersion != LocalAiInstallManifest.CurrentSchemaVersion)
            throw new InvalidDataException($"Unsupported local AI manifest schema version {manifest.SchemaVersion}.");
        if (!string.Equals(manifest.Engine, "ollama", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The local AI manifest engine must be ollama.");
        if (string.IsNullOrWhiteSpace(manifest.EngineVersion))
            throw new InvalidDataException("The local AI manifest engine version is required.");
        if (manifest.Architecture is not ("x64" or "arm64"))
            throw new InvalidDataException("The local AI manifest architecture must be x64 or arm64.");
        if (string.IsNullOrWhiteSpace(manifest.ModelTag))
            throw new InvalidDataException("The local AI manifest model tag is required.");
        if (manifest.ContextLength is <= 0)
            throw new InvalidDataException("The local AI manifest context length must be positive when specified.");

        var executable = _paths.ResolveContainedPath(manifest.ExecutablePath, nameof(manifest.ExecutablePath));
        if (!string.Equals(Path.GetFileName(executable), "ollama.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The managed local AI executable must be ollama.exe.");
        var models = _paths.ResolveContainedPath(manifest.ModelsPath, nameof(manifest.ModelsPath));

        if (!Uri.TryCreate(manifest.Endpoint, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != Uri.UriSchemeHttp ||
            !IsLoopback(endpoint) ||
            endpoint.Port is <= 0 or > 65535 ||
            !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query) ||
            !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw new InvalidDataException("The local AI endpoint must be an HTTP loopback address.");
        }

        return new LocalAiResolvedInstall(manifest, executable, models, endpoint);
    }

    private static bool IsLoopback(Uri endpoint) =>
        string.Equals(endpoint.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        (System.Net.IPAddress.TryParse(endpoint.Host, out var address) && System.Net.IPAddress.IsLoopback(address));
}
