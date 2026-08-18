using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace OpenClaw.SetupEngine.Tests;

public sealed class LocalAiPathPolicyTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(),
        $"local-ai-paths-{Guid.NewGuid():N}");

    public LocalAiPathPolicyTests() => Directory.CreateDirectory(_tempDirectory);

    public void Dispose()
    {
        // slopwatch-ignore: SW003 Test cleanup is best-effort and must not hide the test result.
        try { Directory.Delete(_tempDirectory, recursive: true); } catch { }
    }

    [Fact]
    public void TryResolve_ReturnsVersionedAppOwnedLayout()
    {
        var artifact = OllamaReleasePolicy.Resolve(Architecture.Arm64);

        var resolved = LocalAiPathPolicy.TryResolve(
            _tempDirectory,
            artifact,
            out var paths,
            out var error);

        Assert.True(resolved, error);
        Assert.Equal(Path.Combine(_tempDirectory, "LocalAI"), paths.RootDirectory);
        Assert.Equal(Path.Combine(_tempDirectory, "LocalAI", "downloads"), paths.DownloadsDirectory);
        Assert.Equal(
            Path.Combine(_tempDirectory, "LocalAI", "engines", "ollama", "0.32.14", "win-arm64"),
            paths.EngineDirectory);
        Assert.Equal(Path.Combine(paths.EngineDirectory, "ollama.exe"), paths.EngineExecutablePath);
        Assert.Equal(Path.Combine(_tempDirectory, "LocalAI", "models"), paths.ModelsDirectory);
        Assert.Equal(Path.Combine(paths.DownloadsDirectory, artifact.FileName), paths.ArchivePath);
    }

    [Fact]
    public void TryResolve_RejectsArtifactPathTraversal()
    {
        var pinned = OllamaReleasePolicy.Resolve(Architecture.X64);
        var malicious = pinned with { FileName = @"..\outside.zip" };

        var resolved = LocalAiPathPolicy.TryResolve(
            _tempDirectory,
            malicious,
            out _,
            out var error);

        Assert.False(resolved);
        Assert.Contains("invalid path segment", error);
    }

    [Fact]
    public void TryGetStagingDirectory_AcceptsBoundedRunIdAndRejectsTraversal()
    {
        Assert.True(LocalAiPathPolicy.TryResolve(
            _tempDirectory,
            OllamaReleasePolicy.Resolve(Architecture.X64),
            out var paths,
            out var resolveError), resolveError);

        Assert.True(LocalAiPathPolicy.TryGetStagingDirectory(
            paths,
            "abcdef123456",
            out var staging,
            out var stagingError), stagingError);
        Assert.StartsWith(paths.RootDirectory + Path.DirectorySeparatorChar, staging);

        Assert.False(LocalAiPathPolicy.TryGetStagingDirectory(
            paths,
            @"..\outside",
            out _,
            out var traversalError));
        Assert.Contains("run ID", traversalError);
    }

    [Theory]
    [InlineData("ollama.exe", true)]
    [InlineData("lib/ollama/cuda.dll", true)]
    [InlineData("../outside.exe", false)]
    [InlineData("lib/../../outside.exe", false)]
    [InlineData("C:\\outside.exe", false)]
    public void TryResolveArchiveEntryDestination_ContainsEveryEntry(
        string entryName,
        bool expected)
    {
        var staging = Path.Combine(_tempDirectory, "LocalAI", "engines", "stage");

        var resolved = LocalAiPathPolicy.TryResolveArchiveEntryDestination(
            staging,
            entryName,
            out var destination,
            out _);

        Assert.Equal(expected, resolved);
        if (expected)
            Assert.StartsWith(Path.GetFullPath(staging) + Path.DirectorySeparatorChar, destination);
    }

    [Fact]
    public void TryValidateManagedDeleteTarget_RejectsRootAndOutsidePath()
    {
        var root = Path.Combine(_tempDirectory, "LocalAI");
        var outside = Path.Combine(_tempDirectory, "keep");

        Assert.False(LocalAiPathPolicy.TryValidateManagedDeleteTarget(
            _tempDirectory,
            root,
            out _,
            out var rootError));
        Assert.Contains("not below", rootError);

        Assert.False(LocalAiPathPolicy.TryValidateManagedDeleteTarget(
            _tempDirectory,
            outside,
            out _,
            out var outsideError));
        Assert.Contains("not below", outsideError);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void TryValidateManagedDeleteTarget_RejectsJunctionAncestor()
    {
        var outside = Path.Combine(Path.GetTempPath(), $"local-ai-outside-{Guid.NewGuid():N}");
        var root = Path.Combine(_tempDirectory, "LocalAI");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "keep");

        try
        {
            CreateJunction(root, outside);

            var allowed = LocalAiPathPolicy.TryValidateManagedDeleteTarget(
                _tempDirectory,
                Path.Combine(root, "models"),
                out _,
                out var error);

            Assert.False(allowed);
            Assert.Contains("reparse point", error);
            Assert.True(File.Exists(Path.Combine(outside, "keep.txt")));
        }
        finally
        {
            // slopwatch-ignore: SW003 Test cleanup is best-effort and must not hide the test result.
            try { Directory.Delete(root); } catch { }
            // slopwatch-ignore: SW003 Test cleanup is best-effort and must not hide the test result.
            try { Directory.Delete(outside, recursive: true); } catch { }
        }
    }

    [SupportedOSPlatform("windows")]
    private static void CreateJunction(string link, string target)
    {
        using var process = System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /J \"{link}\" \"{target}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            }) ?? throw new InvalidOperationException("Failed to start mklink.");

        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }
}
