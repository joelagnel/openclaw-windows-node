using OpenClaw.Shared.Inference.Catalog;
using OpenClaw.TestSupport;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using RuntimeArchitecture = System.Runtime.InteropServices.Architecture;

namespace OpenClaw.SetupEngine.Tests;

public sealed class LlamaRuntimeInstallerTests
{
    [Fact]
    public async Task Install_MergesBinaryAndCudaArchivesAndValidatesRuntime()
    {
        using var temp = new TempDirectory("llama-runtime-");
        byte[] binary = Zip(
            ("llama-server.exe", "server"),
            ("ggml-cuda.dll", "cuda"));
        byte[] dependencies = Zip(
            ("cudart64_13.dll", "cudart"),
            ("cublas64_13.dll", "cublas"),
            ("cublasLt64_13.dll", "cublasLt"));
        LlamaRuntimeVariant runtime = Runtime(binary, dependencies);
        using var client = new HttpClient(new ArchiveHandler(binary, dependencies));
        var inspector = new RecordingInspector(new LlamaRuntimeInspection(true, "build 10488", null));

        LlamaRuntimeInstallResult result = await new LlamaRuntimeInstaller(
            new LocalAiArtifactInstaller(client),
            inspector).InstallAsync(
                temp.Path,
                runtime,
                progress: null,
                CancellationToken.None);

        Assert.Equal(LlamaRuntimeInstallDisposition.Installed, result.Disposition);
        Assert.True(result.CreatedThisRun);
        Assert.Equal(2, result.VerifiedArchives.Count);
        Assert.True(File.Exists(Path.Combine(result.InstallDirectory, "llama-server.exe")));
        Assert.True(File.Exists(Path.Combine(result.InstallDirectory, "cudart64_13.dll")));
        Assert.Equal(result.InstallDirectory, inspector.LastDirectory);
    }

    [Fact]
    public async Task Install_RejectsUnclaimedExistingRuntimeWithoutNetwork()
    {
        using var temp = new TempDirectory("llama-runtime-");
        LlamaRuntimeVariant runtime = Runtime(Zip(("a", "a")), Zip(("b", "b")));
        LocalAiComponentIdentity component = LlamaRuntimeInstaller.Component(runtime);
        Assert.True(LocalAiPathPolicy.TryResolve(temp.Path, component, out var paths, out var error), error);
        Directory.CreateDirectory(paths.InstallDirectory);
        using var client = new HttpClient(new ThrowingHandler());

        LocalAiArtifactInstallException failure = await Assert.ThrowsAsync<LocalAiArtifactInstallException>(() =>
            new LlamaRuntimeInstaller(
            new LocalAiArtifactInstaller(client),
            new RecordingInspector(new LlamaRuntimeInspection(true, "build 10488", null))).InstallAsync(
                temp.Path,
                runtime,
                progress: null,
                CancellationToken.None));

        Assert.Contains("unclaimed", failure.Message);
        Assert.True(Directory.Exists(paths.InstallDirectory));
    }

    [Fact]
    public async Task Install_InvalidNewRuntimeRollsBackPromotedDirectory()
    {
        using var temp = new TempDirectory("llama-runtime-");
        byte[] binary = Zip(("llama-server.exe", "server"));
        byte[] dependencies = Zip(("cudart64_13.dll", "cudart"));
        LlamaRuntimeVariant runtime = Runtime(binary, dependencies);
        LocalAiComponentIdentity component = LlamaRuntimeInstaller.Component(runtime);
        Assert.True(LocalAiPathPolicy.TryResolve(temp.Path, component, out var paths, out var error), error);
        using var client = new HttpClient(new ArchiveHandler(binary, dependencies));
        var installer = new LlamaRuntimeInstaller(
            new LocalAiArtifactInstaller(client),
            new RecordingInspector(new LlamaRuntimeInspection(false, null, "invalid runtime")));

        LocalAiArtifactInstallException failure = await Assert.ThrowsAsync<LocalAiArtifactInstallException>(() =>
            installer.InstallAsync(temp.Path, runtime, progress: null, CancellationToken.None));

        Assert.Contains("invalid runtime", failure.Message);
        Assert.False(Directory.Exists(paths.InstallDirectory));
    }

    [Theory]
    [InlineData("version: 0.1.2-dev (build 10488, commit 9d77fa172)", true)]
    [InlineData("version: 0.1.2-dev (build 10487, commit 9d77fa172)", false)]
    [InlineData("version: 0.1.2-dev (build 10488, commit deadbeef0)", false)]
    public void VersionValidationRequiresPinnedBuildAndCommit(string output, bool expected)
    {
        Assert.Equal(expected, WindowsLlamaRuntimeInspector.ValidateVersionOutput(output).IsValid);
    }

    [Theory]
    [InlineData(RuntimeArchitecture.X64, "win-x64")]
    [InlineData(RuntimeArchitecture.Arm64, "win-arm64")]
    public void ComponentUsesExactOsArchitecture(RuntimeArchitecture architecture, string runtimeIdentifier)
    {
        LlamaRuntimeVariant runtime = Runtime(Zip(("a", "a")), Zip(("b", "b")), architecture);

        LocalAiComponentIdentity component = LlamaRuntimeInstaller.Component(runtime);

        Assert.Equal("llama-server", component.Name);
        Assert.Equal("b10488", component.Version);
        Assert.Equal(runtimeIdentifier, component.RuntimeIdentifier);
    }

    private static LlamaRuntimeVariant Runtime(
        byte[] binary,
        byte[] dependencies,
        RuntimeArchitecture architecture = RuntimeArchitecture.Arm64)
    {
        var source = new GitHubReleaseSource(
            "example/runtime",
            "b10488",
            "0123456789abcdef0123456789abcdef01234567");
        return new LlamaRuntimeVariant(
            "test-runtime",
            architecture,
            new Version(13, architecture == RuntimeArchitecture.Arm64 ? 4 : 3),
            [
                Artifact(source, ArtifactRole.RuntimeBinary, "binary.zip", binary),
                Artifact(source, ArtifactRole.RuntimeDependency, "dependencies.zip", dependencies),
            ]);
    }

    private static PinnedArtifact Artifact(
        GitHubReleaseSource source,
        ArtifactRole role,
        string fileName,
        byte[] bytes) =>
        new(
            fileName.Replace(".zip", "", StringComparison.Ordinal),
            role,
            source,
            fileName,
            bytes.Length,
            new Sha256Digest(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()));

    private static byte[] Zip(params (string Name, string Contents)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, string contents) in entries)
            {
                ZipArchiveEntry entry = archive.CreateEntry(name);
                using StreamWriter writer = new(entry.Open());
                writer.Write(contents);
            }
        }
        return stream.ToArray();
    }

    private sealed class RecordingInspector(LlamaRuntimeInspection result) : ILlamaRuntimeInspector
    {
        public string? LastDirectory { get; private set; }

        public Task<LlamaRuntimeInspection> InspectAsync(string installDirectory, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastDirectory = installDirectory;
            return Task.FromResult(result);
        }
    }

    private sealed class ArchiveHandler(byte[] binary, byte[] dependencies) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] bytes = request.RequestUri?.AbsolutePath.EndsWith("binary.zip", StringComparison.Ordinal) == true
                ? binary
                : dependencies;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes),
            };
            response.Content.Headers.ContentLength = bytes.Length;
            return Task.FromResult(response);
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("network must not be used");
    }
}
