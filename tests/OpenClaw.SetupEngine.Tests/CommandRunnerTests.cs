using System.Diagnostics;

namespace OpenClaw.SetupEngine.Tests;

public class CommandRunnerTests
{
    private static readonly string s_largeStdin = new('x', 8 * 1024 * 1024);

    [Fact]
    public async Task RunAsync_LargeStdinWriteObeysTimeout()
    {
        var runner = CreateRunner();
        var (executable, arguments) = SleepingCommand();
        var stopwatch = Stopwatch.StartNew();

        var result = await runner.RunAsync(
            executable,
            arguments,
            TimeSpan.FromMilliseconds(250),
            stdinInput: s_largeStdin);

        Assert.True(result.TimedOut);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RunAsync_LargeStdinWriteObeysCallerCancellation()
    {
        var runner = CreateRunner();
        var (executable, arguments) = SleepingCommand();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(
            executable,
            arguments,
            TimeSpan.FromSeconds(30),
            stdinInput: s_largeStdin,
            ct: cts.Token));

        Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RunAsync_StreamStdinPreservesBinaryBytes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"openclaw-stdin-{Guid.NewGuid():N}.bin");
        var bytes = new byte[] { 0, 1, 2, 0x7F, 0x80, 0xFF };
        var (executable, arguments) = CopyStdinCommand(path);
        await using var input = new MemoryStream(bytes);

        try
        {
            var result = await CreateRunner().RunAsync(
                executable,
                arguments,
                TimeSpan.FromSeconds(15),
                stdinStream: input);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RunAsync_RejectsMultipleStdinSources()
    {
        var (executable, arguments) = SleepingCommand();
        await using var input = new MemoryStream([1]);

        await Assert.ThrowsAsync<ArgumentException>(() => CreateRunner().RunAsync(
            executable,
            arguments,
            TimeSpan.FromSeconds(1),
            stdinInput: "text",
            stdinStream: input));
    }

    [Fact]
    public async Task RunAsync_ReturnsWhenChildExitsEvenWhileAHelperHoldsThePipes()
    {
        // Regression: Process.WaitForExitAsync also waits for the redirected streams to
        // reach EOF, which needs every handle on the write end closed. wsl.exe hands its
        // handles to a service helper that outlives it, so EOF never arrived and each
        // WSL command burned its whole timeout and reported a spurious TimedOut.
        var runner = CreateRunner();
        var (executable, arguments) = ExitsLeavingPipeHolderCommand();
        var stopwatch = Stopwatch.StartNew();

        var result = await runner.RunAsync(executable, arguments, TimeSpan.FromSeconds(30));

        stopwatch.Stop();
        Assert.False(result.TimedOut);
        Assert.Equal(0, result.ExitCode);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(5));
    }

    private static CommandRunner CreateRunner()
        => new(new SetupLogger(filePath: null, LogLevel.Trace));

    /// <summary>
    /// A command that exits immediately but leaves a background descendant holding the
    /// inherited stdout/stderr write handles for far longer than the assertion window.
    /// </summary>
    private static (string Executable, string[] Arguments) ExitsLeavingPipeHolderCommand()
        => OperatingSystem.IsWindows()
            ? ("cmd.exe", ["/d", "/s", "/c", "start /b ping 127.0.0.1 -n 20"])
            : ("/bin/sh", ["-c", "sleep 20 & exit 0"]);

    private static (string Executable, string[] Arguments) SleepingCommand()
        => OperatingSystem.IsWindows()
            ? ("cmd.exe", ["/d", "/s", "/c", "ping 127.0.0.1 -n 30 >nul"])
            : ("/bin/sh", ["-c", "sleep 30"]);

    private static (string Executable, string[] Arguments) CopyStdinCommand(string path)
    {
        if (!OperatingSystem.IsWindows())
            return ("/bin/sh", ["-c", $"cat > '{path.Replace("'", "'\\''", StringComparison.Ordinal)}'"]);

        var escapedPath = path.Replace("'", "''", StringComparison.Ordinal);
        var script =
            $"$inputStream = [Console]::OpenStandardInput(); $output = [IO.File]::Create('{escapedPath}'); " +
            "try { $inputStream.CopyTo($output) } finally { $output.Dispose() }";
        return ("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", script]);
    }
}
