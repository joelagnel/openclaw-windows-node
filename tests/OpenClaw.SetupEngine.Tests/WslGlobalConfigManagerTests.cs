using System.Text;
using OpenClaw.TestSupport;

namespace OpenClaw.SetupEngine.Tests;

public sealed class WslGlobalConfigManagerTests : IDisposable
{
    private readonly TempDirectory _temp = new("openclaw-wslconfig-");

    public WslGlobalConfigManagerTests()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Inspect_MissingFile_IsNotMirrored()
    {
        var manager = CreateManager();

        Assert.Equal(new WslGlobalConfigStatus(false, false), manager.Inspect());
    }

    [Fact]
    public void Apply_AlreadyMirrored_IsNoOpAndCreatesNoBackup()
    {
        File.WriteAllText(ConfigPath, "[wsl2]\r\nnetworkingMode=mirrored\r\n");
        var manager = CreateManager();

        var result = manager.ApplyMirroredNetworking();

        Assert.False(result.Changed);
        Assert.True(manager.Inspect().IsMirrored);
        Assert.False(Directory.Exists(BackupDirectory));
    }

    [Fact]
    public void Apply_MissingFile_CreatesMirroredConfigAndRollbackDeletesIt()
    {
        var manager = CreateManager();

        var result = manager.ApplyMirroredNetworking();

        Assert.True(result.Changed);
        Assert.Equal("[wsl2]\nnetworkingMode=mirrored\n", File.ReadAllText(ConfigPath).Replace("\r\n", "\n"));
        Assert.Equal(WslGlobalConfigRestoreResult.Restored, manager.RestoreIfUnchanged());
        Assert.False(File.Exists(ConfigPath));
    }

    [Fact]
    public void Apply_PreservesCommentsSectionsNewlinesAndBom()
    {
        const string original = "; user comment\r\n[wsl2]\r\nmemory=12GB\r\n\r\n[experimental]\r\nautoMemoryReclaim=gradual\r\n";
        File.WriteAllBytes(ConfigPath, [.. Encoding.UTF8.Preamble, .. Encoding.UTF8.GetBytes(original)]);
        var manager = CreateManager();

        manager.ApplyMirroredNetworking();

        var bytes = File.ReadAllBytes(ConfigPath);
        Assert.True(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        var updated = Encoding.UTF8.GetString(bytes.AsSpan(Encoding.UTF8.Preamble.Length));
        Assert.Equal(
            "; user comment\r\n[wsl2]\r\nmemory=12GB\r\n\r\nnetworkingMode=mirrored\r\n[experimental]\r\nautoMemoryReclaim=gradual\r\n",
            updated);
        Assert.Equal(WslGlobalConfigRestoreResult.Restored, manager.RestoreIfUnchanged());
        Assert.Equal([.. Encoding.UTF8.Preamble, .. Encoding.UTF8.GetBytes(original)], File.ReadAllBytes(ConfigPath));
    }

    [Fact]
    public void Apply_ReplacesExistingNetworkingModeWithoutChangingOtherLines()
    {
        File.WriteAllText(ConfigPath, "[wsl2]\nnetworkingMode = NAT\ndnsTunneling=true\n");
        var manager = CreateManager();

        manager.ApplyMirroredNetworking();

        Assert.Equal("[wsl2]\nnetworkingMode=mirrored\ndnsTunneling=true\n", File.ReadAllText(ConfigPath));
    }

    [Fact]
    public void Restore_UserEditedAppliedFile_PreservesUserChangesAndBackup()
    {
        File.WriteAllText(ConfigPath, "[wsl2]\nnetworkingMode=nat\n");
        var manager = CreateManager();
        manager.ApplyMirroredNetworking();
        File.AppendAllText(ConfigPath, "dnsTunneling=true\n");

        var result = manager.RestoreIfUnchanged();

        Assert.Equal(WslGlobalConfigRestoreResult.UserModified, result);
        Assert.Contains("dnsTunneling=true", File.ReadAllText(ConfigPath));
        Assert.True(File.Exists(Path.Combine(BackupDirectory, "wslconfig.rollback.json")));
    }

    [Theory]
    [InlineData("[wsl2]\nnetworkingMode=nat\n[wsl2]\nmemory=4GB\n")]
    [InlineData("[wsl2]\nnetworkingMode=nat\nnetworkingMode=mirrored\n")]
    [InlineData("[wsl2\nnetworkingMode=nat\n")]
    public void Apply_AmbiguousOrMalformedConfig_FailsClosed(string contents)
    {
        File.WriteAllText(ConfigPath, contents);
        var manager = CreateManager();

        Assert.Throws<InvalidDataException>(() => manager.ApplyMirroredNetworking());
        Assert.Equal(contents, File.ReadAllText(ConfigPath));
        Assert.False(Directory.Exists(BackupDirectory));
    }

    private string ConfigPath => _temp.Combine("profile", ".wslconfig");
    private string BackupDirectory => _temp.Combine("local-ai", "network-backup");

    private WslGlobalConfigManager CreateManager()
    {
        return new WslGlobalConfigManager(ConfigPath, BackupDirectory);
    }
}
