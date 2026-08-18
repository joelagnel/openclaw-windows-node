namespace OpenClaw.SetupEngine;

public sealed class ConfigureLocalAiWslNetworkingStep : SetupStep
{
    internal const string StepId = "configure-local-ai-wsl-networking";
    private readonly Func<SetupContext, IWslGlobalConfigManager> _managerFactory;

    public ConfigureLocalAiWslNetworkingStep()
        : this(CreateManager)
    {
    }

    internal ConfigureLocalAiWslNetworkingStep(Func<SetupContext, IWslGlobalConfigManager> managerFactory)
    {
        _managerFactory = managerFactory ?? throw new ArgumentNullException(nameof(managerFactory));
    }

    public override string Id => StepId;
    public override string DisplayName => "Configure secure WSL networking";
    public override bool CanSkip(SetupContext ctx) => !ctx.Config.LocalAi.Enabled;

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        if (LocalAiSetupPolicy.Validate(ctx.Config.LocalAi) is { } error)
            return StepResult.Terminal(error);

        var manager = _managerFactory(ctx);
        WslGlobalConfigStatus status;
        try
        {
            status = manager.Inspect();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return StepResult.Terminal($"OpenClaw could not inspect the global WSL configuration: {ex.Message}", ex);
        }

        if (status.IsMirrored)
            return StepResult.Ok("WSL mirrored networking is already enabled");
        if (!ctx.Config.LocalAi.AllowGlobalWslNetworkingChange)
        {
            return StepResult.Terminal(
                "Local AI needs WSL mirrored networking. Review and approve the one-time global WSL shutdown in setup.");
        }

        try
        {
            var change = manager.ApplyMirroredNetworking();
            if (!change.Changed)
                return StepResult.Ok("WSL mirrored networking is already enabled");

            var shutdown = await ShutdownWslAsync(ctx, ct);
            if (shutdown.ExitCode != 0)
            {
                _ = manager.RestoreIfUnchanged();
                return StepResult.Fail($"WSL shutdown failed after configuring mirrored networking: {shutdown.Stderr}");
            }

            return StepResult.Ok("WSL mirrored networking enabled");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return StepResult.Fail($"OpenClaw could not configure mirrored WSL networking: {ex.Message}", ex);
        }
    }

    public override async Task RollbackAsync(SetupContext ctx, CancellationToken ct)
    {
        var manager = _managerFactory(ctx);
        var result = manager.RestoreIfUnchanged();
        if (result == WslGlobalConfigRestoreResult.Restored)
        {
            await ShutdownWslAsync(ctx, ct);
        }
        else if (result == WslGlobalConfigRestoreResult.UserModified)
        {
            ctx.Logger.Warn("The global WSL configuration changed after setup. OpenClaw left the user's newer file untouched.");
        }
        else if (result == WslGlobalConfigRestoreResult.InvalidBackup)
        {
            ctx.Logger.Warn("OpenClaw could not validate the saved WSL configuration backup and did not restore it.");
        }
    }

    private static IWslGlobalConfigManager CreateManager(SetupContext ctx)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var configPath = Path.Combine(profile, ".wslconfig");
        var backupDirectory = Path.Combine(ctx.LocalDataDir, "LocalAI", "network-backup");
        return new WslGlobalConfigManager(configPath, backupDirectory);
    }

    private static Task<CommandResult> ShutdownWslAsync(SetupContext ctx, CancellationToken ct) =>
        ctx.Commands.RunAsync(
            WslConstants.WslExePath,
            ["--shutdown"],
            TimeSpan.FromSeconds(30),
            ct: ct);
}
