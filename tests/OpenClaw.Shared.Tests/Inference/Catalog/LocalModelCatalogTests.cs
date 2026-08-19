using OpenClaw.Shared.Inference.Catalog;

namespace OpenClaw.Shared.Tests.Inference.Catalog;

public sealed class LocalModelCatalogTests
{
    public static TheoryData<string, string, string, long, string, string, double> ModelPins => new()
    {
        {
            "qwen3.6-35b-a3b-mtp-q4-k-m",
            "unsloth/Qwen3.6-35B-A3B-MTP-GGUF",
            "5bc3e238d916f48a861bac2f8a1990a0e9b7e98d",
            22_663_387_424,
            "Qwen3.6-35B-A3B-UD-Q4_K_M.gguf",
            "0b21525e972670ed59e1812e170b27c26355381f0656ecc4e25617ece7dac58b",
            0.6
        },
        {
            "qwen3.6-27b-mtp-q4-k-m",
            "unsloth/Qwen3.6-27B-MTP-GGUF",
            "5cb35eb3dcbf52dbce5f87dbc64df6aaffadcace",
            17_106_773_120,
            "Qwen3.6-27B-Q4_K_M.gguf",
            "a7cbd3ecc0e3f9b333edee61ae66bc87ed713c5d49587a8355814722ed329e0f",
            1.0
        },
        {
            "qwen3.5-9b-mtp-q4-k-m",
            "unsloth/Qwen3.5-9B-MTP-GGUF",
            "9716a636ee4bddc3fed678220b7a33dd2a4160ae",
            5_868_826_976,
            "Qwen3.5-9B-Q4_K_M.gguf",
            "e8dd94817e95d6c0939102049d068418269978377b13616c4726235e232841fe",
            1.0
        },
    };

    [Theory]
    [MemberData(nameof(ModelPins))]
    public void ModelMatchesImmutableHuggingFacePin(
        string modelId,
        string repositoryId,
        string revision,
        long sizeBytes,
        string fileName,
        string sha256,
        double temperature)
    {
        LocalModelInfo model = Assert.IsType<LocalModelInfo>(LocalModelCatalog.Find(modelId));
        var source = Assert.IsType<HuggingFaceRevisionSource>(model.Weights.Source);

        Assert.Equal(repositoryId, source.RepositoryId);
        Assert.Equal(revision, source.RevisionSha);
        Assert.Equal(fileName, model.Weights.RelativePath);
        Assert.Equal(sizeBytes, model.Weights.SizeBytes);
        Assert.Equal(sha256, model.Weights.Sha256.Value);
        Assert.Equal(
            $"https://huggingface.co/{repositoryId}/resolve/{revision}/{fileName}?download=true",
            model.Weights.DownloadUri.AbsoluteUri);
        Assert.Equal(3, model.Recipe.SpeculativeDraftMaxTokens);
        Assert.Equal(temperature, model.Recipe.Sampling.Temperature);
        Assert.Equal(0.0, model.Recipe.Sampling.PresencePenalty);
        Assert.Same(LocalInferenceCatalogProvenance.NvidiaCair, model.Weights.CatalogProvenance);
    }

    [Fact]
    public void CatalogHasOneDefaultAndTwoExplicitAlternatives()
    {
        Assert.Equal(3, LocalModelCatalog.Models.Count);
        Assert.Equal(LocalModelCatalog.Qwen35BModelId, LocalModelCatalog.Default.Id);
        Assert.False(LocalModelCatalog.Default.IsExplicitAlternative);
        Assert.Collection(
            LocalModelCatalog.ExplicitAlternatives,
            model => Assert.Equal(LocalModelCatalog.Qwen27BModelId, model.Id),
            model => Assert.Equal(LocalModelCatalog.Qwen9BModelId, model.Id));
        Assert.All(LocalModelCatalog.ExplicitAlternatives, model => Assert.False(model.IsDefault));
    }

    [Fact]
    public void EveryModelUsesFullNativeContextAndF16KvCache()
    {
        Assert.All(
            LocalModelCatalog.Models,
            model =>
            {
                Assert.Equal(262_144, model.Recipe.ContextTokens);
                Assert.Equal(KvCachePrecision.F16, model.Recipe.KeyCachePrecision);
                Assert.Equal(KvCachePrecision.F16, model.Recipe.ValueCachePrecision);
                Assert.Equal(4_096, model.Recipe.BatchTokens);
                Assert.Equal(4_096, model.Recipe.MicroBatchTokens);
                Assert.Equal(1, model.Recipe.ParallelRequests);
                Assert.True(model.Recipe.FlashAttention);
                Assert.True(model.Recipe.OffloadAllLayers);
                Assert.Equal(SpeculativeDecodingMode.DraftMtp, model.Recipe.SpeculativeDecoding);
            });
    }

    [Fact]
    public void EveryModelUsesThinkingSamplerDefaults()
    {
        Assert.All(
            LocalModelCatalog.Models,
            model =>
            {
                Assert.Equal(20, model.Recipe.Sampling.TopK);
                Assert.Equal(0.95, model.Recipe.Sampling.TopP);
                Assert.Equal(0.0, model.Recipe.Sampling.MinP);
                Assert.Equal(1.0, model.Recipe.Sampling.RepetitionPenalty);
            });
    }

    [Fact]
    public void MtpCatalogIsTextOnlyUntilVisionIsSeparatelyQualified()
    {
        Assert.All(LocalModelCatalog.Models, model => Assert.False(model.SupportsVision));
        Assert.DoesNotContain(
            LocalModelCatalog.Models,
            model => model.Weights.RelativePath.Contains("mmproj", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ModelIdsAndFilesAreUniqueAndLookupIsCaseInsensitive()
    {
        Assert.Equal(
            LocalModelCatalog.Models.Count,
            LocalModelCatalog.Models.Select(model => model.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            LocalModelCatalog.Models.Count,
            LocalModelCatalog.Models.Select(model => model.Weights.RelativePath).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            LocalModelCatalog.Qwen35BModelId,
            LocalModelCatalog.Find(LocalModelCatalog.Qwen35BModelId.ToUpperInvariant())?.Id);
        Assert.Null(LocalModelCatalog.Find("qwen3.8-27b"));
        Assert.Null(LocalModelCatalog.Find(null));
    }
}
