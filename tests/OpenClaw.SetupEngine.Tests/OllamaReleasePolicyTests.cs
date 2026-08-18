using System.Runtime.InteropServices;

namespace OpenClaw.SetupEngine.Tests;

public class OllamaReleasePolicyTests
{
    [Fact]
    public void EmbeddedPolicy_IsCompleteAndValid()
    {
        Assert.Empty(OllamaReleasePolicy.ValidateEmbeddedPolicy());
        Assert.Equal(2, OllamaReleasePolicy.Artifacts.Count);
    }

    [Fact]
    public void Resolve_X64_ReturnsPinnedOfficialArtifact()
    {
        var artifact = OllamaReleasePolicy.Resolve(Architecture.X64);

        Assert.Equal("0.32.14", artifact.Version);
        Assert.Equal("win-x64", artifact.RuntimeIdentifier);
        Assert.Equal("ollama-windows-amd64.zip", artifact.FileName);
        Assert.Equal(
            "https://github.com/ollama/ollama/releases/download/v0.32.14/ollama-windows-amd64.zip",
            artifact.DownloadUri.AbsoluteUri);
        Assert.Equal(1_459_874_325, artifact.SizeBytes);
        Assert.Equal(
            "5ae5bca5f0d297f5e35665e01db399a69a8eac3f8fad89cd9d2531fd495c9457",
            artifact.Sha256);
    }

    [Fact]
    public void Resolve_Arm64_ReturnsPinnedOfficialArtifact()
    {
        var artifact = OllamaReleasePolicy.Resolve(Architecture.Arm64);

        Assert.Equal("0.32.14", artifact.Version);
        Assert.Equal("win-arm64", artifact.RuntimeIdentifier);
        Assert.Equal("ollama-windows-arm64.zip", artifact.FileName);
        Assert.Equal(
            "https://github.com/ollama/ollama/releases/download/v0.32.14/ollama-windows-arm64.zip",
            artifact.DownloadUri.AbsoluteUri);
        Assert.Equal(209_894_691, artifact.SizeBytes);
        Assert.Equal(
            "821cdc689f3bb750ab3192fa96189676f8db0eda51e8d01b837ea7581474e1de",
            artifact.Sha256);
    }

    [Theory]
    [InlineData(Architecture.X86)]
    [InlineData(Architecture.Arm)]
    [InlineData(Architecture.Wasm)]
    public void Resolve_UnsupportedArchitecture_FailsClosed(Architecture architecture)
    {
        var error = Assert.Throws<PlatformNotSupportedException>(
            () => OllamaReleasePolicy.Resolve(architecture));

        Assert.Contains(architecture.ToString(), error.Message);
    }
}
