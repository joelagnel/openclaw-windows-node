using OpenClaw.Shared.Inference.Catalog;

namespace OpenClaw.Shared.Tests.Inference.Catalog;

public sealed class PinnedArtifactTests
{
    private const string Revision = "5bc3e238d916f48a861bac2f8a1990a0e9b7e98d";
    private const string Digest = "0b21525e972670ed59e1812e170b27c26355381f0656ecc4e25617ece7dac58b";

    [Fact]
    public void HuggingFaceSourceBuildsRevisionPinnedDownloadUri()
    {
        var source = new HuggingFaceRevisionSource(
            "unsloth/Qwen3.6-35B-A3B-MTP-GGUF",
            Revision);
        var artifact = new PinnedArtifact(
            "qwen-model",
            ArtifactRole.ModelWeights,
            source,
            "UD-Q4_K_M/model weights.gguf",
            123,
            new Sha256Digest(Digest));

        Assert.Equal("https://huggingface.co/unsloth/Qwen3.6-35B-A3B-MTP-GGUF", source.RepositoryUri.AbsoluteUri.TrimEnd('/'));
        Assert.Equal(
            $"https://huggingface.co/unsloth/Qwen3.6-35B-A3B-MTP-GGUF/tree/{Revision}",
            source.RevisionUri.AbsoluteUri);
        Assert.Equal(
            $"https://huggingface.co/unsloth/Qwen3.6-35B-A3B-MTP-GGUF/resolve/{Revision}/UD-Q4_K_M/model%20weights.gguf?download=true",
            artifact.DownloadUri.AbsoluteUri);
        Assert.DoesNotContain("/main/", artifact.DownloadUri.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public void GitHubSourcePinsReleaseTagAndCommit()
    {
        var source = new GitHubReleaseSource(
            "ggml-org/llama.cpp",
            "b10488",
            "9d77fa17254e1dee4b9e92504c91611a60b1359f");
        var artifact = new PinnedArtifact(
            "llama-runtime",
            ArtifactRole.RuntimeBinary,
            source,
            "llama-b10488-bin-win-cuda-13.4-arm64.zip",
            140_379_054,
            new Sha256Digest("75554d62f4af8f4150d3b4b0cca7df62d44105e98fb7cd92ab2d177e382b441d"));

        Assert.Equal("9d77fa17254e1dee4b9e92504c91611a60b1359f", source.ImmutableRevision);
        Assert.Equal("https://github.com/ggml-org/llama.cpp/releases/tag/b10488", source.RevisionUri.AbsoluteUri);
        Assert.Equal(
            "https://github.com/ggml-org/llama.cpp/releases/download/b10488/llama-b10488-bin-win-cuda-13.4-arm64.zip",
            artifact.DownloadUri.AbsoluteUri);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ABC")]
    [InlineData("0b21525e972670ed59e1812e170b27c26355381f0656ecc4e25617ece7dac58B")]
    [InlineData("0b21525e972670ed59e1812e170b27c26355381f0656ecc4e25617ece7dac58")]
    public void Sha256DigestRejectsNonCanonicalValues(string value)
    {
        Assert.Throws<ArgumentException>(() => new Sha256Digest(value));
    }

    [Theory]
    [InlineData("main")]
    [InlineData("5BC3E238D916F48A861BAC2F8A1990A0E9B7E98D")]
    [InlineData("5bc3e238d916f48a861bac2f8a1990a0e9b7e98")]
    public void HuggingFaceSourceRejectsMutableOrMalformedRevision(string revision)
    {
        Assert.Throws<ArgumentException>(() =>
            new HuggingFaceRevisionSource("unsloth/model", revision));
    }

    [Theory]
    [InlineData("../model.gguf")]
    [InlineData("models\\model.gguf")]
    [InlineData("/model.gguf")]
    [InlineData("models//model.gguf")]
    public void PinnedArtifactRejectsUnsafeRelativePath(string relativePath)
    {
        var source = new HuggingFaceRevisionSource("unsloth/model", Revision);

        Assert.Throws<ArgumentException>(() => new PinnedArtifact(
            "model",
            ArtifactRole.ModelWeights,
            source,
            relativePath,
            1,
            new Sha256Digest(Digest)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void PinnedArtifactRejectsNonPositiveSize(long sizeBytes)
    {
        var source = new HuggingFaceRevisionSource("unsloth/model", Revision);

        Assert.Throws<ArgumentOutOfRangeException>(() => new PinnedArtifact(
            "model",
            ArtifactRole.ModelWeights,
            source,
            "model.gguf",
            sizeBytes,
            new Sha256Digest(Digest)));
    }

    [Fact]
    public void NvidiaCairAttributionIsTrackedWithoutPrivateSourceLocation()
    {
        CatalogProvenance provenance = LocalInferenceCatalogProvenance.NvidiaCair;

        Assert.Equal("nvidia-cair", provenance.SourceId);
        Assert.Equal("NVIDIA Corporation", provenance.Creator);
        Assert.Equal("CC-BY-4.0", provenance.LicenseIdentifier);
        Assert.Equal("https://creativecommons.org/licenses/by/4.0/", provenance.LicenseUri.AbsoluteUri);
        Assert.Null(provenance.SourceUri);
        Assert.DoesNotContain("private", provenance.Changes, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("internal", provenance.Changes, StringComparison.OrdinalIgnoreCase);
    }
}
