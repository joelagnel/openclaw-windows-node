using OpenClaw.TestSupport;

namespace OpenClaw.SetupEngine.Tests;

public sealed class LocalAiWslNetworkingStepTests : IDisposable
{
    private readonly TempDirectory _temp = new("openclaw-local-ai-network-");

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task Execute_AlreadyMirrored_DoesNotShutdownWsl()
    {
        var manager = new FakeManager { Status = new(true, true) };
        var commands = new RecordingCommandRunner();
        var step = new ConfigureLocalAiWslNetworkingStep(_ => manager);

        var result = await step.ExecuteAsync(CreateContext(commands, allowChange: false), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Empty(commands.Calls);
        Assert.Equal(0, manager.ApplyCount);
    }

    [Fact]
    public async Task Execute_MissingConsent_FailsWithoutMutation()
    {
        var manager = new FakeManager { Status = new(false, false) };
        var commands = new RecordingCommandRunner();
        var step = new ConfigureLocalAiWslNetworkingStep(_ => manager);

        var result = await step.ExecuteAsync(CreateContext(commands, allowChange: false), CancellationToken.None);

        Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
        Assert.Contains("approve", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, manager.ApplyCount);
        Assert.Empty(commands.Calls);
    }

    [Fact]
    public async Task Execute_ApprovedChange_AppliesAndShutsDownOnce()
    {
        var manager = new FakeManager
        {
            Status = new(true, false),
            ApplyResult = new(true, new(false, new string('0', 64), new string('1', 64))),
        };
        var commands = new RecordingCommandRunner();
        var step = new ConfigureLocalAiWslNetworkingStep(_ => manager);

        var result = await step.ExecuteAsync(CreateContext(commands, allowChange: true), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, manager.ApplyCount);
        var call = Assert.Single(commands.Calls);
        Assert.EndsWith("wsl.exe", call.Executable, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["--shutdown"], call.Arguments);
    }

    [Fact]
    public async Task Rollback_RestoredConfig_ShutsDownToApplyIt()
    {
        var manager = new FakeManager { RestoreResult = WslGlobalConfigRestoreResult.Restored };
        var commands = new RecordingCommandRunner();
        var step = new ConfigureLocalAiWslNetworkingStep(_ => manager);
        var ctx = CreateContext(commands, allowChange: true);

        await step.RollbackAsync(ctx, CancellationToken.None);

        Assert.Equal(1, manager.RestoreCount);
        Assert.Single(commands.Calls);
    }

    [Fact]
    public async Task Rollback_UserModified_DoesNotShutdownOrOverwrite()
    {
        var manager = new FakeManager { RestoreResult = WslGlobalConfigRestoreResult.UserModified };
        var commands = new RecordingCommandRunner();
        var step = new ConfigureLocalAiWslNetworkingStep(_ => manager);

        await step.RollbackAsync(CreateContext(commands, allowChange: true), CancellationToken.None);

        Assert.Equal(1, manager.RestoreCount);
        Assert.Empty(commands.Calls);
    }

    private SetupContext CreateContext(ICommandRunner commands, bool allowChange) =>
        new(
            new SetupConfig
            {
                LocalAi = new LocalAiConfig
                {
                    Enabled = true,
                    AllowGlobalWslNetworkingChange = allowChange,
                },
            },
            new SetupLogger(null, LogLevel.Trace),
            new TransactionJournal(null),
            commands,
            CancellationToken.None,
            dataDir: _temp.Combine("data"),
            localDataDir: _temp.Combine("local"));

    private sealed class FakeManager : IWslGlobalConfigManager
    {
        public WslGlobalConfigStatus Status { get; init; } = new(false, false);
        public WslGlobalConfigApplyResult ApplyResult { get; init; } = new(false, null);
        public WslGlobalConfigRestoreResult RestoreResult { get; init; } = WslGlobalConfigRestoreResult.NoBackup;
        public int ApplyCount { get; private set; }
        public int RestoreCount { get; private set; }
        public WslGlobalConfigStatus Inspect() => Status;
        public WslGlobalConfigApplyResult ApplyMirroredNetworking() { ApplyCount++; return ApplyResult; }
        public WslGlobalConfigRestoreResult RestoreIfUnchanged() { RestoreCount++; return RestoreResult; }
    }

    private sealed class RecordingCommandRunner : ICommandRunner
    {
        public CommandResult Result { get; init; } = new(0, "", "", TimeSpan.Zero, false);
        public List<CommandCall> Calls { get; } = [];

        public Task<CommandResult> RunAsync(
            string executable,
            string[] arguments,
            TimeSpan timeout,
            IReadOnlyDictionary<string, string>? environment = null,
            string? workingDirectory = null,
            string? stdinInput = null,
            CancellationToken ct = default,
            Stream? stdinStream = null)
        {
            Calls.Add(new(executable, arguments));
            return Task.FromResult(Result);
        }

        public Task<CommandResult> RunInWslAsync(
            string distroName,
            string command,
            TimeSpan timeout,
            IReadOnlyDictionary<string, string>? environment = null,
            CancellationToken ct = default,
            string? user = null,
            bool inputViaStdin = false) => throw new NotSupportedException();
    }

    private sealed record CommandCall(string Executable, string[] Arguments);
}
