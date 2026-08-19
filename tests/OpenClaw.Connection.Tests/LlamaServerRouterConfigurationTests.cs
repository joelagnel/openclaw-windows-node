using OpenClaw.Connection.LocalAi;
using OpenClaw.Shared.Inference.Catalog;
using OpenClaw.TestSupport;
using System.Collections.Immutable;

namespace OpenClaw.Connection.Tests;

public sealed class LlamaServerRouterConfigurationTests
{
    [Fact]
    public void Build_StartsOfflineRouterWithoutPreloadingModel()
    {
        using var temp = new TempDirectory("llama-router-");
        var paths = new LocalAiPaths(temp.Path);
        LocalAiResolvedInstall install = Resolve(paths, ManifestFor(LocalModelCatalog.Default));

        LlamaServerRouterLaunchPlan plan = LlamaServerRouterConfiguration.Build(paths, install);

        Assert.Equal(paths.RouterPresetPath, plan.PresetPath);
        Assert.Equal(LocalModelCatalog.Default.Id, plan.ModelAlias);
        Assert.Equal(install.Manifest.SelectedGpuId, Assert.Contains("CUDA_VISIBLE_DEVICES", plan.Environment));
        Assert.DoesNotContain("--model", plan.Arguments);
        Assert.DoesNotContain(install.ModelPath, plan.Arguments);
        Assert.Contains("--models-autoload", plan.Arguments);
        Assert.Contains("--offline", plan.Arguments);
        Assert.Contains("--no-webui", plan.Arguments);
        Assert.Contains("--metrics", plan.Arguments);
        AssertArgumentPair(plan.Arguments, "--host", "127.0.0.1");
        AssertArgumentPair(plan.Arguments, "--port", "18803");
        AssertArgumentPair(plan.Arguments, "--models-preset", paths.RouterPresetPath);
        AssertArgumentPair(plan.Arguments, "--models-max", "1");
        Assert.Contains("load-on-startup = false", plan.PresetContent, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(LocalModelCatalog.Qwen35BModelId, "temperature = 0.6")]
    [InlineData(LocalModelCatalog.Qwen27BModelId, "temperature = 1")]
    [InlineData(LocalModelCatalog.Qwen9BModelId, "temperature = 1")]
    public void Build_EmitsQualifiedCairRecipeForEveryChoice(string modelId, string expectedTemperature)
    {
        using var temp = new TempDirectory("llama-router-");
        var paths = new LocalAiPaths(temp.Path);
        LocalModelInfo model = Assert.Single(LocalModelCatalog.Models, candidate => candidate.Id == modelId);
        LocalAiResolvedInstall install = Resolve(paths, ManifestFor(model));

        string preset = LlamaServerRouterConfiguration.Build(paths, install).PresetContent;

        Assert.Contains($"[{model.Id}]", preset, StringComparison.Ordinal);
        Assert.Contains($"model = {install.ModelPath}", preset, StringComparison.Ordinal);
        Assert.Contains("ctx-size = 262144", preset, StringComparison.Ordinal);
        Assert.Contains("batch-size = 4096", preset, StringComparison.Ordinal);
        Assert.Contains("ubatch-size = 4096", preset, StringComparison.Ordinal);
        Assert.Contains("parallel = 1", preset, StringComparison.Ordinal);
        Assert.Contains("cache-type-k = f16", preset, StringComparison.Ordinal);
        Assert.Contains("cache-type-v = f16", preset, StringComparison.Ordinal);
        Assert.Contains("gpu-layers = all", preset, StringComparison.Ordinal);
        Assert.Contains("split-mode = none", preset, StringComparison.Ordinal);
        Assert.Contains("fit = off", preset, StringComparison.Ordinal);
        Assert.Contains("load-mode = dio", preset, StringComparison.Ordinal);
        Assert.Contains("spec-type = draft-mtp", preset, StringComparison.Ordinal);
        Assert.Contains("spec-draft-n-max = 3", preset, StringComparison.Ordinal);
        Assert.Contains(expectedTemperature, preset, StringComparison.Ordinal);
        Assert.Contains("top-k = 20", preset, StringComparison.Ordinal);
        Assert.Contains("top-p = 0.95", preset, StringComparison.Ordinal);
        Assert.Contains("min-p = 0", preset, StringComparison.Ordinal);
        Assert.Contains("repeat-penalty = 1", preset, StringComparison.Ordinal);
        Assert.Contains("presence-penalty = 0", preset, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_RejectsManifestThatDriftsFromQualifiedCatalog()
    {
        using var temp = new TempDirectory("llama-router-");
        var paths = new LocalAiPaths(temp.Path);
        LocalAiInstallManifest manifest = ManifestFor(LocalModelCatalog.Default);

        LocalAiInstallManifest[] invalid =
        [
            manifest with { HardwareProfileId = "geforce-rtx-5090" },
            manifest with { RuntimeId = LlamaRuntimeCatalog.X64RuntimeId },
            manifest with { ModelAlias = LocalModelCatalog.Qwen27BModelId },
            manifest with { ContextLength = 32_768 },
            manifest with
            {
                RuntimeAssets = manifest.RuntimeAssets.SetItem(
                    0,
                    manifest.RuntimeAssets[0] with { SizeBytes = manifest.RuntimeAssets[0].SizeBytes - 1 }),
            },
            manifest with { ModelAsset = manifest.ModelAsset with { SizeBytes = manifest.ModelAsset.SizeBytes - 1 } },
        ];

        foreach (LocalAiInstallManifest candidate in invalid)
        {
            LocalAiResolvedInstall install = Resolve(paths, candidate);
            Assert.Throws<InvalidDataException>(() => LlamaServerRouterConfiguration.Build(paths, install));
        }
    }

    private static LocalAiResolvedInstall Resolve(LocalAiPaths paths, LocalAiInstallManifest manifest) =>
        new LocalAiManifestStore(paths).ResolveAndValidate(manifest);

    private static LocalAiInstallManifest ManifestFor(LocalModelInfo model)
    {
        var manifest = LocalAiManifestStoreTests.ValidManifest();
        var source = Assert.IsType<HuggingFaceRevisionSource>(model.Weights.Source);
        return manifest with
        {
            ModelCatalogId = model.Id,
            ModelPath = Path.Combine("models", Path.GetFileName(model.Weights.RelativePath)),
            ModelId = $"{source.RepositoryId}@{source.RevisionSha}",
            ModelAlias = model.Id,
            ModelAsset = new LocalAiAssetReceipt
            {
                FileName = Path.GetFileName(model.Weights.RelativePath),
                SourceUrl = model.Weights.DownloadUri.AbsoluteUri,
                SizeBytes = model.Weights.SizeBytes,
                Sha256 = model.Weights.Sha256.Value,
            },
            ContextLength = model.Recipe.ContextTokens,
        };
    }

    private static void AssertArgumentPair(ImmutableArray<string> arguments, string name, string value)
    {
        int index = arguments.IndexOf(name);
        Assert.True(index >= 0 && index + 1 < arguments.Length, $"Missing argument {name}.");
        Assert.Equal(value, arguments[index + 1]);
    }
}
