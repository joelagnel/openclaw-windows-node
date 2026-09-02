using OpenClaw.TestSupport;

namespace OpenClaw.SetupEngine.Tests;

/// <summary>
/// The mirrored-networking step is the only Local AI setup step allowed to rewrite
/// the user-wide .wslconfig and issue a global <c>wsl.exe --shutdown</c>. Enabling
/// Local AI alone must never authorize that. Without explicit consent the step has
/// to stop before touching anything, so a denied user keeps their exact .wslconfig
/// bytes and their running distributions.
/// </summary>
public sealed class LocalAiWslNetworkingConsentTests
{
    [Fact]
    public async Task Step_WithoutConsent_LeavesWslConfigUntouchedAndIssuesNoShutdown()
    {
        using var temp = new TempDirectory("local-ai-consent-deny-");
        string configPath = Path.Combine(temp.Path, ".wslconfig");
        const string original = "[wsl2]\r\nnetworkingMode=NAT\r\nmemory=8GB\r\n";
        await File.WriteAllTextAsync(configPath, original);
        byte[] before = await File.ReadAllBytesAsync(configPath);

        var manager = new RecordingManager(configPath);
        SetupContext context = CreateContext(temp.Path, consent: false);
        var step = new ConfigureLocalAiWslNetworkingStep(_ => manager);

        StepResult result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
        Assert.False(manager.ApplyCalled);
        Assert.False(manager.RestoreCalled);
        Assert.Equal(before, await File.ReadAllBytesAsync(configPath));
        Assert.Equal(original, await File.ReadAllTextAsync(configPath));
        // Denial must not leave a backup or staged copy behind either.
        Assert.Equal([configPath], Directory.GetFiles(temp.Path));
    }

    [Fact]
    public async Task Step_EnablingLocalAiAloneDoesNotImplyConsent()
    {
        using var temp = new TempDirectory("local-ai-consent-implicit-");
        string configPath = Path.Combine(temp.Path, ".wslconfig");
        await File.WriteAllTextAsync(configPath, "[wsl2]\r\nnetworkingMode=NAT\r\n");

        var manager = new RecordingManager(configPath);
        // Local AI is enabled, but consent was never granted.
        SetupContext context = CreateContext(temp.Path, consent: false);
        Assert.True(context.Config.LocalAi.Enabled);
        Assert.False(context.Config.LocalAi.WslMirroredNetworkingConsent);

        StepResult result = await new ConfigureLocalAiWslNetworkingStep(_ => manager)
            .ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
        Assert.False(manager.ApplyCalled);
    }

    private static SetupContext CreateContext(string localDataDirectory, bool consent)
    {
        var config = new SetupConfig
        {
            LocalAi = new LocalAiConfig
            {
                Enabled = true,
                WslMirroredNetworkingConsent = consent,
            },
        };
        var logger = new SetupLogger(filePath: null, LogLevel.Trace);
        return new SetupContext(
            config,
            logger,
            new TransactionJournal(filePath: null),
            new CommandRunner(logger),
            CancellationToken.None,
            localDataDir: localDataDirectory);
    }

    private sealed class RecordingManager(string configPath) : IWslGlobalConfigManager
    {
        public bool ApplyCalled { get; private set; }
        public bool RestoreCalled { get; private set; }

        public WslGlobalConfigStatus Inspect() => new(File.Exists(configPath), false);

        public WslGlobalConfigApplyResult ApplyMirroredNetworking()
        {
            ApplyCalled = true;
            throw new InvalidOperationException(
                "The step must not apply mirrored networking without explicit consent.");
        }

        public WslGlobalConfigRestoreResult RestoreIfUnchanged()
        {
            RestoreCalled = true;
            return WslGlobalConfigRestoreResult.NoBackup;
        }
    }
}
