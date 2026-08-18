using System.Runtime.Versioning;
using OpenClaw.TestSupport;

namespace OpenClaw.SetupEngine.Tests;

public sealed class LocalAiPathPolicyTests : IDisposable
{
    private readonly TempDirectory _temp = new("local-ai-paths-");

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void TryResolve_ReturnsVersionedAppOwnedLayout()
    {
        var identity = new LocalAiComponentIdentity("native-engine", "1.2.3", "win-arm64");

        var resolved = LocalAiPathPolicy.TryResolve(
            _temp.Path,
            identity,
            out var paths,
            out var error);

        Assert.True(resolved, error);
        Assert.Equal(_temp.Combine("LocalAI"), paths.RootDirectory);
        Assert.Equal(_temp.Combine("LocalAI", "downloads"), paths.DownloadsDirectory);
        Assert.Equal(_temp.Combine("LocalAI", "engines"), paths.EnginesDirectory);
        Assert.Equal(
            _temp.Combine("LocalAI", "engines", "native-engine", "1.2.3", "win-arm64"),
            paths.InstallDirectory);
        Assert.Equal(_temp.Combine("LocalAI", "models"), paths.ModelsDirectory);
        Assert.Equal(_temp.Combine("LocalAI", "logs"), paths.LogsDirectory);
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("C:\\outside")]
    [InlineData("CON")]
    [InlineData("name.")]
    [InlineData(" name")]
    public void TryResolve_RejectsUnsafeIdentitySegments(string componentName)
    {
        var identity = new LocalAiComponentIdentity(componentName, "1.2.3", "win-x64");

        var resolved = LocalAiPathPolicy.TryResolve(
            _temp.Path,
            identity,
            out _,
            out var error);

        Assert.False(resolved);
        Assert.Contains("invalid path segment", error);
    }

    [Fact]
    public void TryGetDownloadPath_ResolvesMultipleDistinctArchives()
    {
        var paths = ResolvePaths();

        Assert.True(LocalAiPathPolicy.TryGetDownloadPath(
            paths,
            "runtime.zip",
            out var runtimeArchive,
            out var runtimeError), runtimeError);
        Assert.True(LocalAiPathPolicy.TryGetDownloadPath(
            paths,
            "dependencies.zip",
            out var dependencyArchive,
            out var dependencyError), dependencyError);

        Assert.Equal(_temp.Combine("LocalAI", "downloads", "runtime.zip"), runtimeArchive);
        Assert.Equal(_temp.Combine("LocalAI", "downloads", "dependencies.zip"), dependencyArchive);
        Assert.NotEqual(runtimeArchive, dependencyArchive);
    }

    [Theory]
    [InlineData("../outside.zip")]
    [InlineData("payload.zip:stream")]
    [InlineData("NUL.zip")]
    [InlineData("nested/payload.zip")]
    public void TryGetDownloadPath_RejectsUnsafeArchiveFileName(string fileName)
    {
        var resolved = LocalAiPathPolicy.TryGetDownloadPath(
            ResolvePaths(),
            fileName,
            out _,
            out var error);

        Assert.False(resolved);
        Assert.Contains("invalid path segment", error);
    }

    [Fact]
    public void TryGetStagingDirectory_AcceptsBoundedRunIdAndRejectsTraversal()
    {
        var paths = ResolvePaths();

        Assert.True(LocalAiPathPolicy.TryGetStagingDirectory(
            paths,
            "abcdef123456",
            out var staging,
            out var stagingError), stagingError);
        Assert.StartsWith(
            paths.StagingDirectory + Path.DirectorySeparatorChar,
            staging,
            StringComparison.OrdinalIgnoreCase);

        Assert.False(LocalAiPathPolicy.TryGetStagingDirectory(
            paths,
            "../outside",
            out _,
            out var traversalError));
        Assert.Contains("run ID", traversalError);
    }

    [Fact]
    public void TryGetModelPaths_UsesImmutableRepositoryRevisionLayout()
    {
        var paths = ResolvePaths();
        const string revision = "5bc3e238d916f48a861bac2f8a1990a0e9b7e98d";

        var resolved = LocalAiPathPolicy.TryGetModelPaths(
            paths,
            "unsloth/Qwen3.6-35B-A3B-MTP-GGUF",
            revision,
            "Qwen3.6-35B-A3B-UD-Q4_K_M.gguf",
            out var model,
            out var partial,
            out var error);

        Assert.True(resolved, error);
        Assert.Equal(
            _temp.Combine(
                "LocalAI",
                "models",
                "unsloth",
                "Qwen3.6-35B-A3B-MTP-GGUF",
                revision,
                "Qwen3.6-35B-A3B-UD-Q4_K_M.gguf"),
            model);
        Assert.Equal(model + ".partial", partial);
    }

    [Theory]
    [InlineData("unsloth/model", "main", "model.gguf")]
    [InlineData("unsloth/../model", "5bc3e238d916f48a861bac2f8a1990a0e9b7e98d", "model.gguf")]
    [InlineData("unsloth/model", "5bc3e238d916f48a861bac2f8a1990a0e9b7e98d", "../model.gguf")]
    [InlineData("unsloth/model", "5bc3e238d916f48a861bac2f8a1990a0e9b7e98d", "model.bin")]
    public void TryGetModelPaths_RejectsMutableOrUnsafeIdentity(
        string repositoryId,
        string revision,
        string fileName)
    {
        Assert.False(LocalAiPathPolicy.TryGetModelPaths(
            ResolvePaths(),
            repositoryId,
            revision,
            fileName,
            out _,
            out _,
            out var error));
        Assert.Contains("invalid path segment", error);
    }

    [Theory]
    [InlineData("bin/native.exe", true)]
    [InlineData("lib/cuda.dll", true)]
    [InlineData("../outside.exe", false)]
    [InlineData("lib/../../outside.exe", false)]
    [InlineData("C:\\outside.exe", false)]
    public void TryResolveArchiveEntryDestination_ContainsEveryEntry(
        string entryName,
        bool expected)
    {
        var staging = _temp.Combine("LocalAI", "staging", "abcdef12");

        var resolved = LocalAiPathPolicy.TryResolveArchiveEntryDestination(
            staging,
            entryName,
            out var destination,
            out _);

        Assert.Equal(expected, resolved);
        if (expected)
        {
            Assert.StartsWith(
                Path.GetFullPath(staging) + Path.DirectorySeparatorChar,
                destination,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void TryValidateManagedDeleteTarget_RejectsRootAndOutsidePath()
    {
        var root = _temp.Combine("LocalAI");
        var outside = _temp.Combine("keep");

        Assert.False(LocalAiPathPolicy.TryValidateManagedDeleteTarget(
            _temp.Path,
            root,
            out _,
            out var rootError));
        Assert.Contains("not below", rootError);

        Assert.False(LocalAiPathPolicy.TryValidateManagedDeleteTarget(
            _temp.Path,
            outside,
            out _,
            out var outsideError));
        Assert.Contains("not below", outsideError);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void TryValidateManagedDeleteTarget_RejectsJunctionAncestor()
    {
        using var outside = new TempDirectory("local-ai-outside-");
        var root = _temp.Combine("LocalAI");
        File.WriteAllText(outside.Combine("keep.txt"), "keep");

        try
        {
            CreateJunction(root, outside.Path);

            var allowed = LocalAiPathPolicy.TryValidateManagedDeleteTarget(
                _temp.Path,
                Path.Combine(root, "models"),
                out _,
                out var error);

            Assert.False(allowed);
            Assert.Contains("reparse point", error);
            Assert.True(File.Exists(outside.Combine("keep.txt")));
        }
        finally
        {
            // slopwatch-ignore: SW003 Test cleanup is best-effort and must not hide the test result.
            try { Directory.Delete(root); } catch { }
        }
    }

    private LocalAiSetupPaths ResolvePaths()
    {
        Assert.True(LocalAiPathPolicy.TryResolve(
            _temp.Path,
            new LocalAiComponentIdentity("native-engine", "1.2.3", "win-x64"),
            out var paths,
            out var error), error);
        return paths;
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
