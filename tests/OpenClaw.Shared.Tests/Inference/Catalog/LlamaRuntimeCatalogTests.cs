using OpenClaw.Shared.Inference.Catalog;
using RuntimeArchitecture = System.Runtime.InteropServices.Architecture;

namespace OpenClaw.Shared.Tests.Inference.Catalog;

public sealed class LlamaRuntimeCatalogTests
{
    [Fact]
    public void ReleaseSourcePinsTagAndCommit()
    {
        Assert.Equal("b10488", LlamaRuntimeCatalog.ReleaseTag);
        Assert.Equal("9d77fa17254e1dee4b9e92504c91611a60b1359f", LlamaRuntimeCatalog.ReleaseCommitSha);
        Assert.Equal("https://github.com/ggml-org/llama.cpp/releases/tag/b10488", LlamaRuntimeCatalog.Source.RevisionUri.AbsoluteUri);
    }

    [Theory]
    [InlineData(RuntimeArchitecture.X64, "b10488-cuda13-x64", 13, 3, 537_794_998L)]
    [InlineData(RuntimeArchitecture.Arm64, "b10488-cuda13-arm64", 13, 4, 293_697_851L)]
    public void VariantMatchesQualifiedWindowsRuntime(
        RuntimeArchitecture architecture,
        string expectedId,
        int cudaMajor,
        int cudaMinor,
        long expectedDownloadBytes)
    {
        LlamaRuntimeVariant variant = Assert.IsType<LlamaRuntimeVariant>(LlamaRuntimeCatalog.Find(architecture));

        Assert.Equal(expectedId, variant.Id);
        Assert.Equal(new Version(cudaMajor, cudaMinor), variant.CudaVersion);
        Assert.Equal(expectedDownloadBytes, variant.TotalDownloadSizeBytes);
        Assert.Equal(2, variant.Artifacts.Count);
        Assert.Single(variant.Artifacts, artifact => artifact.Role == ArtifactRole.RuntimeBinary);
        Assert.Single(variant.Artifacts, artifact => artifact.Role == ArtifactRole.RuntimeDependency);
    }

    [Theory]
    [InlineData(
        "llama-b10488-bin-win-cuda-13.3-x64.zip",
        146_824_581L,
        "f4ea53c2e7f3d295cb9fd092515d50af4969266b4cdae01f03a1cbaa8b4d9af0")]
    [InlineData(
        "cudart-llama-bin-win-cuda-13.3-x64.zip",
        390_970_417L,
        "1462a050eb4c684921ba51dcc4cc488a036674c3e73e9945ee705b854808d03e")]
    [InlineData(
        "llama-b10488-bin-win-cuda-13.4-arm64.zip",
        140_379_054L,
        "75554d62f4af8f4150d3b4b0cca7df62d44105e98fb7cd92ab2d177e382b441d")]
    [InlineData(
        "cudart-llama-bin-win-cuda-13.4-arm64.zip",
        153_318_797L,
        "5a40dc7c5fa3d0a80ceeba4f16f9e8d25d87bcf1399c9233588953c43436c33c")]
    public void ArtifactMatchesGitHubReleaseMetadata(string fileName, long sizeBytes, string sha256)
    {
        PinnedArtifact artifact = Assert.Single(
            LlamaRuntimeCatalog.Variants.SelectMany(variant => variant.Artifacts),
            candidate => candidate.RelativePath == fileName);

        Assert.Equal(sizeBytes, artifact.SizeBytes);
        Assert.Equal(sha256, artifact.Sha256.Value);
        Assert.Equal(
            $"https://github.com/ggml-org/llama.cpp/releases/download/b10488/{fileName}",
            artifact.DownloadUri.AbsoluteUri);
        Assert.Same(LocalInferenceCatalogProvenance.NvidiaCair, artifact.CatalogProvenance);
    }

    [Fact]
    public void CatalogContainsOnlyNativeNvidiaWindowsVariants()
    {
        Assert.Collection(
            LlamaRuntimeCatalog.Variants.OrderBy(variant => variant.Architecture),
            variant => Assert.Equal(RuntimeArchitecture.X64, variant.Architecture),
            variant => Assert.Equal(RuntimeArchitecture.Arm64, variant.Architecture));
        Assert.DoesNotContain(
            LlamaRuntimeCatalog.Variants,
            variant => variant.Id.Contains("cpu", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            LlamaRuntimeCatalog.Variants,
            variant => variant.Id.Contains("vulkan", StringComparison.OrdinalIgnoreCase));
        Assert.Null(LlamaRuntimeCatalog.Find(RuntimeArchitecture.X86));
    }

    [Fact]
    public void CatalogIdentifiersAndArtifactPathsAreUnique()
    {
        Assert.Equal(
            LlamaRuntimeCatalog.Variants.Count,
            LlamaRuntimeCatalog.Variants.Select(variant => variant.Id).Distinct(StringComparer.Ordinal).Count());

        PinnedArtifact[] artifacts = LlamaRuntimeCatalog.Variants.SelectMany(variant => variant.Artifacts).ToArray();
        Assert.Equal(artifacts.Length, artifacts.Select(artifact => artifact.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            artifacts.Length,
            artifacts.Select(artifact => artifact.RelativePath).Distinct(StringComparer.Ordinal).Count());
    }
}
