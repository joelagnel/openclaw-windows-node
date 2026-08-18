using OpenClaw.Connection.LocalAi;
using OpenClaw.Shared;
using OpenClaw.TestSupport;
using System.Diagnostics;

namespace OpenClaw.Connection.Tests;

public sealed class LocalAiManagedProcessHostTests
{
    [Fact]
    public void CreateStartInfo_PreservesExplicitArgumentsIncludingSpaces()
    {
        using var temp = new TempDirectory("local-ai-process-");
        var spec = MakeSpec(
            temp,
            executablePath: @"C:\Program Files\OpenClaw Local AI\llama-server.exe",
            arguments:
            [
                "--model",
                @"C:\Models With Spaces\Qwen model.gguf",
                "--alias",
                "qwen local",
            ]);

        var startInfo = WindowsLocalAiManagedProcessHost.CreateStartInfo(spec);

        Assert.Empty(startInfo.Arguments);
        Assert.Equal(spec.Arguments, startInfo.ArgumentList);
        Assert.Equal("value with spaces", startInfo.Environment["OPENCLAW_TEST_VALUE"]);
        Assert.False(startInfo.UseShellExecute);
    }

    [Fact]
    public async Task StartProcessAsync_RejectsPreCanceledOperationWithoutStarting()
    {
        using var temp = new TempDirectory("local-ai-process-");
        var host = new WindowsLocalAiManagedProcessHost(NullLogger.Instance);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            host.StartProcessAsync(
                MakePowerShellSleepSpec(temp),
                _ => { },
                cancellation.Token));

        Assert.False(File.Exists(temp.Combine("llama-server.stdout.log")));
    }

    [Fact]
    public async Task DisposeAsync_KillsManagedProcessAndReportsExactIdentity()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var temp = new TempDirectory("local-ai-process-");
        var host = new WindowsLocalAiManagedProcessHost(NullLogger.Instance);
        var exited = new TaskCompletionSource<LocalAiManagedProcessExit>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var beforeStart = DateTimeOffset.UtcNow.AddSeconds(-2);
        var process = await host.StartProcessAsync(
            MakePowerShellSleepSpec(temp),
            value => exited.TrySetResult(value),
            CancellationToken.None);
        var processId = process.ProcessId;

        Assert.True(processId > 0);
        Assert.InRange(process.StartedAtUtc, beforeStart, DateTimeOffset.UtcNow.AddSeconds(2));
        Assert.False(process.HasExited);

        await process.DisposeAsync();
        var exit = await exited.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(processId, exit.ProcessId);
        Assert.Equal(process.StartedAtUtc, exit.StartedAtUtc);
        await AssertProcessIsGoneAsync(processId);
        await process.DisposeAsync();
    }

    [Fact]
    public void BoundedLog_SanitizesTokensTruncatesLinesAndRotates()
    {
        using var temp = new TempDirectory("local-ai-log-");
        var path = temp.Combine("llama-server.stdout.log");
        using var writer = new BoundedRotatingLogWriter(
            path,
            maxBytes: 1024,
            backupCount: 2,
            maxLineCharacters: 256,
            NullLogger.Instance);
        const string secret = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        for (var index = 0; index < 12; index++)
            writer.WriteLine($"{index:D2}:" + new string('z', 300));
        writer.WriteLine($"Authorization: Bearer {secret}\r\nforged-entry");

        var allLogs = Directory.EnumerateFiles(temp.Path, "llama-server.stdout.log*")
            .Select(File.ReadAllText)
            .ToArray();
        var combined = string.Join("", allLogs);

        Assert.DoesNotContain(secret, combined, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", combined, StringComparison.Ordinal);
        Assert.Contains("[truncated]", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("\r\nforged-entry", combined, StringComparison.Ordinal);
        Assert.True(File.Exists(path + ".1"));
        Assert.All(
            Directory.EnumerateFiles(temp.Path, "llama-server.stdout.log*"),
            file => Assert.InRange(new FileInfo(file).Length, 1, 1024));
    }

    private static LocalAiProcessStartSpec MakePowerShellSleepSpec(TempDirectory temp)
    {
        var executable = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        return MakeSpec(
            temp,
            executable,
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30"]);
    }

    private static LocalAiProcessStartSpec MakeSpec(
        TempDirectory temp,
        string executablePath,
        IReadOnlyList<string> arguments) =>
        new(
            executablePath,
            temp.Path,
            arguments,
            new Dictionary<string, string>
            {
                ["OPENCLAW_TEST_VALUE"] = "value with spaces",
            },
            temp.Combine("llama-server.stdout.log"),
            temp.Combine("llama-server.stderr.log"),
            MaxLogBytes: 4096,
            LogBackupCount: 2,
            MaxLogLineCharacters: 1024);

    private static async Task AssertProcessIsGoneAsync(int processId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited)
                    return;
            }
            catch (ArgumentException)
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail($"Managed process {processId} was still present after disposal.");
    }
}
