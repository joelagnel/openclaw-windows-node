using System.Net;
using System.Net.Sockets;
using OpenClaw.Connection.LocalAi;
using OpenClaw.Shared.Inference;
using OpenClaw.Shared.Inference.Catalog;

namespace OpenClaw.SetupEngine;

internal interface ILocalAiPortSelector
{
    bool TrySelect(int requestedPort, out int selectedPort, out string? error);
}

internal sealed class LoopbackLocalAiPortSelector : ILocalAiPortSelector
{
    public bool TrySelect(int requestedPort, out int selectedPort, out string? error)
    {
        selectedPort = 0;
        error = null;
        if (requestedPort is < 0 or > 65_535)
        {
            error = "The local inference port must be zero or between 1 and 65535.";
            return false;
        }

        try
        {
            var listener = new TcpListener(IPAddress.Loopback, requestedPort)
            {
                ExclusiveAddressUse = true,
            };
            listener.Start();
            selectedPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return true;
        }
        catch (Exception ex) when (ex is SocketException or UnauthorizedAccessException)
        {
            error = requestedPort == 0
                ? "Windows could not allocate a loopback port for local inference."
                : $"Loopback port {requestedPort} is not available for local inference.";
            return false;
        }
    }
}

/// <summary>
/// Selects one qualified SKU/runtime/model plan before setup mutates WSL,
/// downloads artifacts, or changes gateway configuration.
/// </summary>
public sealed class PreflightLocalAiHardwareStep : SetupStep
{
    private readonly IHostHardwareProbe _hardwareProbe;
    private readonly ILocalAiPortSelector _portSelector;

    public PreflightLocalAiHardwareStep()
        : this(new NvmlHostHardwareProbe(), new LoopbackLocalAiPortSelector())
    {
    }

    internal PreflightLocalAiHardwareStep(
        IHostHardwareProbe hardwareProbe,
        ILocalAiPortSelector portSelector)
    {
        _hardwareProbe = hardwareProbe ?? throw new ArgumentNullException(nameof(hardwareProbe));
        _portSelector = portSelector ?? throw new ArgumentNullException(nameof(portSelector));
    }

    public override string Id => "preflight-local-ai-hardware";
    public override string DisplayName => "Checking Local AI compatibility";
    public override bool CanRetry => false;
    public override RetryPolicy Retry => RetryPolicy.None;

    public override bool CanSkip(SetupContext ctx) => !ctx.Config.LocalAi.Enabled;

    public override Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        HostHardwareInfo hardware;
        try
        {
            hardware = _hardwareProbe.Probe();
        }
        catch (Exception ex)
        {
            return Task.FromResult(StepResult.Terminal(
                "Local AI hardware detection failed. No setup changes were made.",
                ex));
        }

        LocalInferenceEligibilityResult eligibility = LocalInferenceEligibility.Evaluate(
            hardware,
            ctx.Config.LocalAi.SelectedModelId);
        ctx.LocalAiHardware = hardware;
        ctx.LocalAiEligibility = eligibility;

        if (eligibility.Status == LocalInferenceEligibilityStatus.Unsupported)
        {
            return Task.FromResult(StepResult.Terminal(
                $"This system does not match a qualified Local AI recipe " +
                $"({eligibility.FailureCode}, {eligibility.SelectionFailureCode})."));
        }

        if (eligibility.Status == LocalInferenceEligibilityStatus.EligibleButBusy)
        {
            long requiredMiB = eligibility.RequiredFreeMemoryBytes / (1024 * 1024);
            long availableMiB = (eligibility.AvailableFreeMemoryBytes ?? 0) / (1024 * 1024);
            return Task.FromResult(StepResult.Terminal(
                $"The selected GPU is supported but currently busy. Local AI needs {requiredMiB:N0} MiB free; " +
                $"{availableMiB:N0} MiB is available. Close GPU applications and retry."));
        }

        if (eligibility.Plan is null || eligibility.SelectedGpu is null)
        {
            return Task.FromResult(StepResult.Terminal(
                "Local AI compatibility was inconclusive. No setup changes were made."));
        }

        if (!_portSelector.TrySelect(ctx.Config.LocalAi.Port, out int port, out string? portError))
            return Task.FromResult(StepResult.Terminal(portError ?? "Local inference port selection failed."));

        ctx.LocalAiPort = port;
        ctx.Logger.Info(
            "Selected qualified Local AI plan",
            new
            {
                profile = eligibility.Plan.HardwareProfile.Id,
                runtime = eligibility.Plan.Runtime.Id,
                model = eligibility.Plan.Model.Id,
                selection = eligibility.Plan.ModelSelectionOrigin.ToString(),
                gpu = eligibility.SelectedGpu.StableId,
                port,
            });

        return Task.FromResult(StepResult.Ok(
            $"Selected {eligibility.Plan.Model.DisplayName} for {eligibility.Plan.HardwareProfile.DisplayName}."));
    }
}

/// <summary>
/// Enables mirrored WSL networking only with explicit consent. This is the
/// sole Local AI setup step allowed to issue a global WSL shutdown.
/// </summary>
public sealed class ConfigureLocalAiWslNetworkingStep : SetupStep
{
    private readonly Func<SetupContext, IWslGlobalConfigManager> _managerFactory;

    public ConfigureLocalAiWslNetworkingStep()
        : this(CreateManager)
    {
    }

    internal ConfigureLocalAiWslNetworkingStep(
        Func<SetupContext, IWslGlobalConfigManager> managerFactory) =>
        _managerFactory = managerFactory ?? throw new ArgumentNullException(nameof(managerFactory));

    public override string Id => "configure-local-ai-wsl-networking";
    public override string DisplayName => "Configuring Local AI access from WSL";
    public override bool CanRetry => false;
    public override RetryPolicy Retry => RetryPolicy.None;

    public override bool CanSkip(SetupContext ctx) => !ctx.Config.LocalAi.Enabled;

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        IWslGlobalConfigManager manager = _managerFactory(ctx);
        WslGlobalConfigStatus status;
        try
        {
            status = manager.Inspect();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return StepResult.Terminal(
                $"The WSL configuration could not be safely inspected: {ex.Message}",
                ex);
        }

        if (status.IsMirrored)
            return StepResult.Skip("WSL mirrored networking is already enabled.");

        if (!ctx.Config.LocalAi.WslMirroredNetworkingConsent)
        {
            return StepResult.Terminal(
                "Local AI requires WSL mirrored networking. Consent is required because applying it stops all running WSL distributions once; no distributions are deleted.");
        }

        WslGlobalConfigApplyResult apply;
        try
        {
            apply = manager.ApplyMirroredNetworking();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return StepResult.Terminal(
                $"WSL mirrored networking could not be configured: {ex.Message}",
                ex);
        }

        if (!apply.Changed)
            return StepResult.Skip("WSL mirrored networking is already enabled.");

        try
        {
            CommandResult shutdown = await ShutdownWslAsync(ctx, ct);
            if (shutdown.ExitCode != 0 || shutdown.TimedOut)
            {
                RestoreAfterFailedApply(manager, ctx);
                return StepResult.Fail(
                    "WSL mirrored networking was restored because WSL could not be stopped to apply it.");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (manager.RestoreIfUnchanged() == WslGlobalConfigRestoreResult.Restored)
                await ShutdownWslAsync(ctx, CancellationToken.None);
            throw;
        }

        return StepResult.Ok("WSL mirrored networking is enabled.");
    }

    public override async Task RollbackAsync(SetupContext ctx, CancellationToken ct)
    {
        IWslGlobalConfigManager manager = _managerFactory(ctx);
        WslGlobalConfigRestoreResult restore = manager.RestoreIfUnchanged();
        switch (restore)
        {
            case WslGlobalConfigRestoreResult.NoBackup:
                return;
            case WslGlobalConfigRestoreResult.UserModified:
                ctx.Logger.Warn("Preserving the user's newer .wslconfig instead of restoring the setup backup.");
                return;
            case WslGlobalConfigRestoreResult.InvalidBackup:
                throw new InvalidDataException("The Local AI WSL configuration backup is invalid.");
            case WslGlobalConfigRestoreResult.Restored:
                CommandResult shutdown = await ShutdownWslAsync(ctx, ct);
                if (shutdown.ExitCode != 0 || shutdown.TimedOut)
                    throw new InvalidOperationException("WSL could not be stopped to apply the restored configuration.");
                return;
            default:
                throw new InvalidOperationException($"Unknown WSL configuration restore result: {restore}.");
        }
    }

    private static IWslGlobalConfigManager CreateManager(SetupContext ctx)
    {
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string configPath = Path.Combine(userProfile, ".wslconfig");
        string backupDirectory = Path.Combine(
            new LocalAiPaths(ctx.LocalDataDir).RootDirectory,
            "wsl-networking");
        return new WslGlobalConfigManager(configPath, backupDirectory);
    }

    private static Task<CommandResult> ShutdownWslAsync(SetupContext ctx, CancellationToken ct) =>
        ctx.Commands.RunAsync(
            WslConstants.WslExePath,
            ["--shutdown"],
            TimeSpan.FromSeconds(60),
            ct: ct);

    private static void RestoreAfterFailedApply(IWslGlobalConfigManager manager, SetupContext ctx)
    {
        WslGlobalConfigRestoreResult restore = manager.RestoreIfUnchanged();
        if (restore != WslGlobalConfigRestoreResult.Restored)
        {
            ctx.Logger.Error(
                $"Failed to restore .wslconfig after WSL shutdown failed: {restore}.");
        }
    }
}

/// <summary>Installs the two pinned llama.cpp runtime archives as one atomic component.</summary>
public sealed class AcquireLocalAiRuntimeStep : SetupStep
{
    private static readonly HttpClient s_httpClient = new(new SocketsHttpHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private readonly ILlamaRuntimeAcquirer _acquirer;

    public AcquireLocalAiRuntimeStep()
        : this(new LlamaRuntimeInstaller(s_httpClient))
    {
    }

    internal AcquireLocalAiRuntimeStep(ILlamaRuntimeAcquirer acquirer) =>
        _acquirer = acquirer ?? throw new ArgumentNullException(nameof(acquirer));

    public override string Id => "acquire-local-ai-runtime";
    public override string DisplayName => "Installing llama-server";
    public override bool CanRetry => false;
    public override RetryPolicy Retry => RetryPolicy.None;

    public override bool CanSkip(SetupContext ctx) => !ctx.Config.LocalAi.Enabled;

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        if (ctx.LocalAiEligibility?.Plan is not { } plan)
            return StepResult.Terminal("Local AI runtime installation requires a qualified hardware plan.");
        if (ctx.Config.LocalAi.AcquisitionTimeoutSeconds <= 0)
            return StepResult.Terminal("The Local AI acquisition timeout must be greater than zero.");

        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(ctx.Config.LocalAi.AcquisitionTimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            LlamaRuntimeInstallResult install = await _acquirer.InstallAsync(
                ctx.LocalDataDir,
                plan.Runtime,
                progress: null,
                linked.Token);
            ctx.LocalAiRuntimeInstall = install;
            return StepResult.Ok($"Installed llama-server {LlamaRuntimeCatalog.ReleaseTag}.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            return StepResult.Fail("The llama-server download timed out.", ex);
        }
        catch (Exception ex) when (
            ex is LocalAiArtifactInstallException
            or IOException
            or UnauthorizedAccessException
            or HttpRequestException)
        {
            return StepResult.Fail($"llama-server installation failed: {ex.Message}", ex);
        }
    }

    public override Task RollbackAsync(SetupContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (ctx.LocalAiRuntimeInstall is { } install)
        {
            _acquirer.RemoveInstalledRuntime(ctx.LocalDataDir, install);
            ctx.LocalAiRuntimeInstall = null;
        }

        return Task.CompletedTask;
    }
}
