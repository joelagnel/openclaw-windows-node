using System.Diagnostics;
using OpenClaw.Shared;

namespace OpenClaw.SetupEngine;

internal enum WslViabilityKind
{
    Ready,
    Installable,
    UpdateRequired,
    EnvironmentBlocked,
    InspectionFailed,
}

internal sealed record WslViabilityResult(
    WslViabilityKind Kind,
    string Summary,
    string Remediation)
{
    public bool BlocksSetup => Kind is
        WslViabilityKind.UpdateRequired or
        WslViabilityKind.EnvironmentBlocked or
        WslViabilityKind.InspectionFailed;

    public string Description => string.IsNullOrWhiteSpace(Remediation)
        ? Summary
        : $"{Summary} {Remediation}";
}

/// <summary>
/// Performs a read-only WSL inspection. This type never installs WSL, changes
/// optional Windows features, updates .wslconfig, or stops a distribution.
/// </summary>
internal static class WslViabilityInspector
{
    public static async Task<WslViabilityResult> InspectAsync(
        ICommandRunner commands,
        SetupLogger logger,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(logger);

        CommandResult versionResult;
        try
        {
            versionResult = await commands.RunAsync(
                WslConstants.WslExePath,
                ["--version"],
                TimeSpan.FromSeconds(5),
                ct: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Warn($"WSL version inspection failed: {ex.Message}");
            return InspectionFailed();
        }

        if (versionResult.ExitCode != 0)
        {
            if (LooksUnavailable(versionResult))
            {
                return new(
                    WslViabilityKind.Installable,
                    "WSL is not installed yet.",
                    "Setup can request administrator approval to install it after Local AI is verified.");
            }

            if (LooksTooOldForVersionCommand(versionResult))
            {
                return new(
                    WslViabilityKind.UpdateRequired,
                    "The installed WSL version is too old for a clean app-owned gateway.",
                    WslInstallSupport.UpdateInstructions);
            }

            logger.Warn($"WSL version inspection returned exit code {versionResult.ExitCode}: " +
                NormalizeWslOutput($"{versionResult.Stdout}\n{versionResult.Stderr}").Trim());
            return InspectionFailed();
        }

        var versionOutput = NormalizeWslOutput($"{versionResult.Stdout}\n{versionResult.Stderr}");
        if (!WslInstallSupport.TryParseWslVersion(versionOutput, out var wslVersion))
        {
            return new(
                WslViabilityKind.UpdateRequired,
                "The installed WSL version could not be verified.",
                WslInstallSupport.UpdateInstructions);
        }

        if (!WslInstallSupport.SupportsDirectNamedInstall(wslVersion))
        {
            return new(
                WslViabilityKind.UpdateRequired,
                $"WSL {wslVersion} cannot create a clean app-owned OpenClaw gateway.",
                WslInstallSupport.UpdateInstructions);
        }

        logger.Info($"WSL version output: {NormalizeWslOutput(versionResult.Stdout).Trim()}");
        logger.Info($"WSL direct named install is supported (version {wslVersion})");

        CommandResult status;
        try
        {
            status = await commands.RunAsync(
                WslConstants.WslExePath,
                ["--status"],
                TimeSpan.FromSeconds(10),
                ct: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Warn($"WSL status inspection failed: {ex.Message}");
            return InspectionFailed();
        }

        var combined = $"{status.Stdout}\n{status.Stderr}";
        if (WslInstallSupport.TryGetEnvironmentIssue(combined, out var message))
        {
            logger.Warn($"WSL environment issue detected: {NormalizeWslOutput(combined).Trim()}");
            return new(
                WslViabilityKind.EnvironmentBlocked,
                "Windows cannot currently start WSL2.",
                message);
        }

        if (status.ExitCode != 0 || status.TimedOut)
        {
            logger.Warn($"WSL status inspection returned exit code {status.ExitCode}: " +
                NormalizeWslOutput(combined).Trim());
            return InspectionFailed();
        }

        return new(
            WslViabilityKind.Ready,
            $"WSL {wslVersion} is ready.",
            string.Empty);
    }

    private static WslViabilityResult InspectionFailed() => new(
        WslViabilityKind.InspectionFailed,
        "OpenClaw could not safely verify the WSL2 environment.",
        "Run wsl --status in PowerShell, resolve the reported problem, and try setup again.");

    internal static bool LooksUnavailable(CommandResult result)
    {
        var text = NormalizeWslOutput($"{result.Stdout}\n{result.Stderr}");
        return text.Contains("aka.ms/wslinstall", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Windows Subsystem for Linux has no installed distributions", StringComparison.OrdinalIgnoreCase)
            || text.Contains("not recognized", StringComparison.OrdinalIgnoreCase)
            || text.Contains("not installed", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksTooOldForVersionCommand(CommandResult result)
    {
        var text = NormalizeWslOutput($"{result.Stdout}\n{result.Stderr}");
        return text.Contains("Invalid command line option", StringComparison.OrdinalIgnoreCase)
            || text.Contains("unrecognized option", StringComparison.OrdinalIgnoreCase)
            || text.Contains("unknown option", StringComparison.OrdinalIgnoreCase);
    }

    internal static string NormalizeWslOutput(string value) => WslInstallSupport.Normalize(value);
}

public sealed class PreflightWslStep : SetupStep
{
    public override string Id => "preflight-wsl";
    public override string DisplayName => "Inspect WSL compatibility";
    public override bool CanRetry => false;

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        WslViabilityResult viability = await WslViabilityInspector.InspectAsync(
            ctx.Commands,
            ctx.Logger,
            ct);
        ctx.WslViability = viability;
        return viability.BlocksSetup
            ? StepResult.Terminal(viability.Description)
            : StepResult.Ok(viability.Description);
    }

    internal static async Task<string?> DetectEnvironmentIssueAsync(SetupContext ctx, CancellationToken ct)
    {
        var status = await ctx.Commands.RunAsync(
            WslConstants.WslExePath,
            ["--status"],
            TimeSpan.FromSeconds(10),
            ct: ct);
        var combined = $"{status.Stdout}\n{status.Stderr}";
        if (!WslInstallSupport.TryGetEnvironmentIssue(combined, out var message))
            return null;

        ctx.Logger.Warn($"WSL environment issue detected: {WslViabilityInspector.NormalizeWslOutput(combined).Trim()}");
        return message;
    }

    internal static Task<StepResult> InstallWslPlatformAsync(SetupContext ctx, CancellationToken ct)
        => InstallWslPlatformAsync(ctx, WslPlatformInstallDiagnostics.QueryGitHubQuotaAsync, ct);

    internal static async Task<StepResult> InstallWslPlatformAsync(
        SetupContext ctx,
        Func<CancellationToken, Task<GitHubApiQuota?>> quotaProbe,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(quotaProbe);

        // wsl --install resolves its download through the GitHub API. When that
        // quota is already spent the install cannot succeed, so fail before
        // raising an administrator prompt the user would approve for nothing.
        GitHubApiQuota? quota = await quotaProbe(ct);
        if (quota is { IsExhausted: true })
        {
            ctx.Logger.Warn(
                $"GitHub API quota exhausted ({quota.Used}/{quota.Limit}, resets {quota.ResetsAt.ToLocalTime():HH:mm}); " +
                "skipping the elevated WSL platform install because its download would be refused");
            return StepResult.Fail(WslPlatformInstallDiagnostics.DescribeUnavailableDownload(quota));
        }

        ctx.Logger.Warn("WSL platform appears to be missing; launching elevated WSL platform install");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = WslConstants.WslExePath,
                UseShellExecute = true,
                Verb = "runas",
                CreateNoWindow = true,
                WorkingDirectory = WslConstants.SafeWindowsWorkingDirectory
            };
            psi.ArgumentList.Add("--install");
            psi.ArgumentList.Add("--no-distribution");

            using var process = Process.Start(psi);
            if (process == null)
                return StepResult.Fail("Could not start elevated WSL platform installer.");

            await process.WaitForExitAsync(ct);

            if (process.ExitCode == 3010)
                return StepResult.Terminal("WSL platform install requires a restart. Reboot Windows, then run setup again.");

            if (process.ExitCode != 0)
            {
                // The installer runs elevated through ShellExecute, which cannot
                // redirect its output, so wsl.exe's own error text is unreachable
                // here. Re-read the quota to name the most common cause instead.
                GitHubApiQuota? postFailureQuota = await quotaProbe(ct);
                ctx.Logger.Warn(
                    $"Elevated WSL platform install exited with code {process.ExitCode}; " +
                    (postFailureQuota is null
                        ? "GitHub API quota could not be read"
                        : $"GitHub API quota {postFailureQuota.Used}/{postFailureQuota.Limit}"));
                return StepResult.Fail(
                    WslPlatformInstallDiagnostics.DescribeFailure(process.ExitCode, postFailureQuota));
            }

            var probe = await ctx.Commands.RunAsync(
                WslConstants.WslExePath,
                ["--version"],
                TimeSpan.FromSeconds(5),
                ct: ct);
            if (probe.ExitCode != 0 || WslViabilityInspector.LooksUnavailable(probe))
            {
                return StepResult.Terminal(
                    "WSL platform install completed, but Windows still reports WSL unavailable. Reboot Windows, then run setup again.");
            }

            return StepResult.Ok("WSL platform installed");
        }
        catch (System.ComponentModel.Win32Exception ex) when ((uint)ex.NativeErrorCode == 1223)
        {
            return StepResult.Fail("WSL platform install was cancelled at the elevation prompt.");
        }
        catch (Exception ex)
        {
            return StepResult.Fail($"WSL platform install failed: {ex.Message}", ex);
        }
    }
}

/// <summary>Performs the first WSL mutation, after native Local AI verification.</summary>
public sealed class EnsureWslPlatformStep : SetupStep
{
    private readonly Func<SetupContext, CancellationToken, Task<StepResult>> _installer;

    public EnsureWslPlatformStep()
        : this(PreflightWslStep.InstallWslPlatformAsync)
    {
    }

    internal EnsureWslPlatformStep(
        Func<SetupContext, CancellationToken, Task<StepResult>> installer) =>
        _installer = installer ?? throw new ArgumentNullException(nameof(installer));

    public override string Id => "ensure-wsl-platform";
    public override string DisplayName => "Prepare WSL platform";

    // Inspection and wsl --install are both idempotent, and the common failures
    // here (exhausted GitHub quota, a declined elevation prompt) are transient.
    public override bool CanRetry => true;

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        WslViabilityResult viability = await WslViabilityInspector.InspectAsync(
            ctx.Commands,
            ctx.Logger,
            ct);
        ctx.WslViability = viability;

        if (viability.Kind == WslViabilityKind.Ready)
            return StepResult.Ok("WSL platform is ready.");
        if (viability.BlocksSetup)
            return StepResult.Terminal(viability.Description);

        StepResult install = await _installer(ctx, ct);
        if (!install.IsSuccess)
            return install;

        viability = await WslViabilityInspector.InspectAsync(ctx.Commands, ctx.Logger, ct);
        ctx.WslViability = viability;
        return viability.Kind == WslViabilityKind.Ready
            ? StepResult.Ok("WSL platform installed and verified.")
            : StepResult.Terminal(viability.Description);
    }
}
