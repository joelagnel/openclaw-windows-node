using OpenClaw.Connection.LocalAi;
using OpenClaw.TestSupport;

namespace OpenClaw.Connection.Tests;

public sealed class LocalAiManifestStoreTests
{
    [Fact]
    public async Task SaveAndLoad_RoundTripsValidatedContainedPathsAtomically()
    {
        using var temp = new TempDirectory("local-ai-manifest-");
        var paths = new LocalAiPaths(temp.Path);
        var store = new LocalAiManifestStore(paths);
        var manifest = ValidManifest();

        await store.SaveAsync(manifest);
        var loaded = await store.LoadAsync();

        Assert.NotNull(loaded);
        Assert.Equal(Path.Combine(paths.RootDirectory, "engines", "ollama", "ollama.exe"), loaded.ExecutablePath);
        Assert.Equal(Path.Combine(paths.RootDirectory, "models"), loaded.ModelsPath);
        Assert.Empty(Directory.EnumerateFiles(paths.RootDirectory, "*.tmp"));
    }

    [Theory]
    [InlineData("..\\outside\\ollama.exe")]
    [InlineData("C:\\outside\\ollama.exe")]
    public async Task Save_RejectsExecutableOutsideManagedRoot(string executablePath)
    {
        using var temp = new TempDirectory("local-ai-manifest-");
        var store = new LocalAiManifestStore(new LocalAiPaths(temp.Path));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.SaveAsync(ValidManifest() with { ExecutablePath = executablePath }));
    }

    [Fact]
    public async Task Save_RejectsNonLoopbackEndpoint()
    {
        using var temp = new TempDirectory("local-ai-manifest-");
        var store = new LocalAiManifestStore(new LocalAiPaths(temp.Path));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.SaveAsync(ValidManifest() with { Endpoint = "http://192.0.2.10:11434" }));
    }

    [Fact]
    public async Task Save_RejectsExistingReparsePointInsideManagedRoot()
    {
        using var temp = new TempDirectory("local-ai-manifest-");
        using var outside = new TempDirectory("local-ai-outside-");
        var paths = new LocalAiPaths(temp.Path);
        Directory.CreateDirectory(paths.RootDirectory);
        var engines = Path.Combine(paths.RootDirectory, "engines");
        try { Directory.CreateSymbolicLink(engines, outside.Path); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new LocalAiManifestStore(paths).SaveAsync(ValidManifest()));
    }

    [Fact]
    public async Task Load_RejectsMalformedJson()
    {
        using var temp = new TempDirectory("local-ai-manifest-");
        var paths = new LocalAiPaths(temp.Path);
        Directory.CreateDirectory(paths.RootDirectory);
        await File.WriteAllTextAsync(paths.ManifestPath, "{not-json");

        await Assert.ThrowsAsync<InvalidDataException>(() => new LocalAiManifestStore(paths).LoadAsync());
    }

    internal static LocalAiInstallManifest ValidManifest() => new()
    {
        EngineVersion = "0.11.7",
        Architecture = "x64",
        ExecutablePath = Path.Combine("engines", "ollama", "ollama.exe"),
        ModelsPath = "models",
        ModelTag = "qwen3:8b",
        Endpoint = "http://127.0.0.1:11434",
        InstalledAtUtc = DateTimeOffset.Parse("2026-08-17T12:00:00Z"),
    };
}
