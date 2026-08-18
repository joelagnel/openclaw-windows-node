using OpenClaw.Connection.LocalAi;
using OpenClaw.TestSupport;
using System.Collections.Immutable;

namespace OpenClaw.Connection.Tests;

public sealed class LocalAiManifestStoreTests
{
    [Fact]
    public async Task SaveAndLoad_RoundTripsValidatedContainedPathsAtomically()
    {
        using var temp = new TempDirectory("local-ai-manifest-");
        var paths = new LocalAiPaths(temp.Path);
        var store = new LocalAiManifestStore(paths);

        await store.SaveAsync(ValidManifest());
        var loaded = await store.LoadAsync();

        Assert.NotNull(loaded);
        Assert.Equal(
            Path.Combine(paths.RootDirectory, "engines", "llama-b10488", "llama-server.exe"),
            loaded.ExecutablePath);
        Assert.Equal(
            Path.Combine(paths.RootDirectory, "models", "Qwen3.6-35B-A3B-UD-Q4_K_M.gguf"),
            loaded.ModelPath);
        Assert.Equal(18803, loaded.Endpoint.Port);
        Assert.Equal(2, loaded.Manifest.RuntimeAssets.Length);
        Assert.Contains(
            loaded.Manifest.RuntimeAssets,
            asset => asset.FileName == "cudart-llama-bin-win-cuda-13.4-arm64.zip");
        Assert.Empty(Directory.EnumerateFiles(paths.RootDirectory, "*.tmp"));
        Assert.Empty(Directory.EnumerateFiles(paths.RootDirectory, ".*.tmp"));
    }

    [Fact]
    public async Task Load_RejectsSchemaV1OllamaManifest()
    {
        using var temp = new TempDirectory("local-ai-manifest-");
        var paths = new LocalAiPaths(temp.Path);
        Directory.CreateDirectory(paths.RootDirectory);
        await File.WriteAllTextAsync(
            paths.ManifestPath,
            """
            {
              "schemaVersion": 1,
              "engine": "ollama",
              "engineVersion": "0.11.7",
              "architecture": "arm64",
              "executablePath": "engines/ollama/ollama.exe",
              "modelsPath": "models",
              "modelTag": "qwen3:8b",
              "endpoint": "http://127.0.0.1:11434"
            }
            """);

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => new LocalAiManifestStore(paths).LoadAsync());

        Assert.Contains("unsupported format", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Save_RejectsOllamaEngineEvenAtCurrentSchema()
    {
        using var temp = new TempDirectory("local-ai-manifest-");
        var store = new LocalAiManifestStore(new LocalAiPaths(temp.Path));

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => store.SaveAsync(ValidManifest() with { Engine = "ollama" }));

        Assert.Contains("llama-server", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("..\\outside\\llama-server.exe", "models\\Qwen3.6-35B-A3B-UD-Q4_K_M.gguf")]
    [InlineData("C:\\outside\\llama-server.exe", "models\\Qwen3.6-35B-A3B-UD-Q4_K_M.gguf")]
    [InlineData("engines\\llama-b10488\\llama-server.exe", "..\\outside\\model.gguf")]
    public async Task Save_RejectsPathsOutsideManagedRoot(string executablePath, string modelPath)
    {
        using var temp = new TempDirectory("local-ai-manifest-");
        var store = new LocalAiManifestStore(new LocalAiPaths(temp.Path));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.SaveAsync(ValidManifest() with { ExecutablePath = executablePath, ModelPath = modelPath }));
    }

    [Theory]
    [InlineData("http://192.0.2.10:18803")]
    [InlineData("https://127.0.0.1:18803")]
    [InlineData("http://user:secret@127.0.0.1:18803")]
    public async Task Save_RejectsEndpointThatIsNotPlainHttpLoopback(string endpoint)
    {
        using var temp = new TempDirectory("local-ai-manifest-");
        var store = new LocalAiManifestStore(new LocalAiPaths(temp.Path));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.SaveAsync(ValidManifest() with { Endpoint = endpoint }));
    }

    [Fact]
    public async Task Save_AcceptsDynamicLoopbackPort()
    {
        using var temp = new TempDirectory("local-ai-manifest-");
        var store = new LocalAiManifestStore(new LocalAiPaths(temp.Path));

        await store.SaveAsync(ValidManifest() with { Endpoint = "http://127.0.0.1:18808/v1" });
        var loaded = await store.LoadAsync();

        Assert.Equal(18808, loaded?.Endpoint.Port);
        Assert.Equal("/v1", loaded?.Endpoint.AbsolutePath);
    }

    [Fact]
    public async Task Save_RejectsExistingReparsePointInsideManagedRoot()
    {
        using var temp = new TempDirectory("local-ai-manifest-");
        using var outside = new TempDirectory("local-ai-outside-");
        var paths = new LocalAiPaths(temp.Path);
        Directory.CreateDirectory(paths.RootDirectory);
        var engines = Path.Combine(paths.RootDirectory, "engines");
        try
        {
            Directory.CreateSymbolicLink(engines, outside.Path);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new LocalAiManifestStore(paths).SaveAsync(ValidManifest()));
    }

    [Fact]
    public async Task Save_RejectsInexactAssetReceipt()
    {
        using var temp = new TempDirectory("local-ai-manifest-");
        var store = new LocalAiManifestStore(new LocalAiPaths(temp.Path));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.SaveAsync(ValidManifest() with
            {
                ModelAsset = ValidManifest().ModelAsset with { Sha256 = "ABC" },
            }));
    }

    [Fact]
    public async Task Save_RejectsMissingRuntimeAssetReceipts()
    {
        using var temp = new TempDirectory("local-ai-manifest-");
        var store = new LocalAiManifestStore(new LocalAiPaths(temp.Path));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.SaveAsync(ValidManifest() with
            {
                RuntimeAssets = ImmutableArray<LocalAiAssetReceipt>.Empty,
            }));
    }

    [Fact]
    public async Task Save_RejectsDuplicateRuntimeAssetFilenamesCaseInsensitively()
    {
        using var temp = new TempDirectory("local-ai-manifest-");
        var store = new LocalAiManifestStore(new LocalAiPaths(temp.Path));
        var runtimeAsset = ValidManifest().RuntimeAssets[0];

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.SaveAsync(ValidManifest() with
            {
                RuntimeAssets =
                [
                    runtimeAsset,
                    runtimeAsset with { FileName = runtimeAsset.FileName.ToUpperInvariant() },
                ],
            }));
    }

    [Fact]
    public async Task Load_RejectsMalformedJson()
    {
        using var temp = new TempDirectory("local-ai-manifest-");
        var paths = new LocalAiPaths(temp.Path);
        Directory.CreateDirectory(paths.RootDirectory);
        await File.WriteAllTextAsync(paths.ManifestPath, "{not-json");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new LocalAiManifestStore(paths).LoadAsync());
    }

    internal static LocalAiInstallManifest ValidManifest() => new()
    {
        EngineVersion = "b10488",
        Architecture = "arm64",
        ExecutablePath = Path.Combine("engines", "llama-b10488", "llama-server.exe"),
        RuntimeAssets =
        [
            new LocalAiAssetReceipt
            {
                FileName = "llama-b10488-bin-win-cuda-13.4-arm64.zip",
                SourceUrl = "https://github.com/ggml-org/llama.cpp/releases/download/b10488/llama-b10488-bin-win-cuda-13.4-arm64.zip",
                SizeBytes = 140_379_054,
                Sha256 = "75554d62f4af8f4150d3b4b0cca7df62d44105e98fb7cd92ab2d177e382b441d",
            },
            new LocalAiAssetReceipt
            {
                FileName = "cudart-llama-bin-win-cuda-13.4-arm64.zip",
                SourceUrl = "https://github.com/ggml-org/llama.cpp/releases/download/b10488/cudart-llama-bin-win-cuda-13.4-arm64.zip",
                SizeBytes = 153_318_797,
                Sha256 = "5a40dc7c5fa3d0a80ceeba4f16f9e8d25d87bcf1399c9233588953c43436c33c",
            },
        ],
        ModelPath = Path.Combine("models", "Qwen3.6-35B-A3B-UD-Q4_K_M.gguf"),
        ModelId = "unsloth/Qwen3.6-35B-A3B-MTP-GGUF@5bc3e238d916f48a861bac2f8a1990a0e9b7e98d",
        ModelAlias = "qwen3.6-35b-a3b-q4",
        ModelAsset = new LocalAiAssetReceipt
        {
            FileName = "Qwen3.6-35B-A3B-UD-Q4_K_M.gguf",
            SourceUrl = "https://huggingface.co/unsloth/Qwen3.6-35B-A3B-MTP-GGUF/resolve/5bc3e238d916f48a861bac2f8a1990a0e9b7e98d/Qwen3.6-35B-A3B-UD-Q4_K_M.gguf",
            SizeBytes = 22_663_387_424,
            Sha256 = "0b21525e972670ed59e1812e170b27c26355381f0656ecc4e25617ece7dac58b",
        },
        Endpoint = "http://127.0.0.1:18803/v1",
        ContextLength = 262_144,
        InstalledAtUtc = DateTimeOffset.Parse("2026-08-18T12:00:00Z"),
    };
}
