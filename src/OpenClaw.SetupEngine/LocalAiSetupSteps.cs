using System.Net;
using System.Net.Sockets;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
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

/// <summary>Downloads one immutable, recipe-selected GGUF directly from Hugging Face.</summary>
public sealed class AcquireLocalAiModelStep : SetupStep
{
    private static readonly HttpClient s_httpClient = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = System.Net.DecompressionMethods.None,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private readonly IHuggingFaceModelAcquirer _acquirer;

    public AcquireLocalAiModelStep()
        : this(new HuggingFaceModelInstaller(s_httpClient))
    {
    }

    internal AcquireLocalAiModelStep(IHuggingFaceModelAcquirer acquirer) =>
        _acquirer = acquirer ?? throw new ArgumentNullException(nameof(acquirer));

    public override string Id => "acquire-local-ai-model";
    public override string DisplayName => "Downloading Local AI model from Hugging Face";
    public override bool CanRetry => false;
    public override RetryPolicy Retry => RetryPolicy.None;

    public override bool CanSkip(SetupContext ctx) => !ctx.Config.LocalAi.Enabled;

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        if (ctx.LocalAiEligibility?.Plan is not { } plan)
            return StepResult.Terminal("Local AI model download requires a qualified hardware plan.");
        if (ctx.LocalAiRuntimeInstall is null)
            return StepResult.Terminal("Local AI model download requires the pinned llama-server runtime.");
        if (ctx.Config.LocalAi.AcquisitionTimeoutSeconds <= 0)
            return StepResult.Terminal("The Local AI acquisition timeout must be greater than zero.");

        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(ctx.Config.LocalAi.AcquisitionTimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            HuggingFaceModelInstallResult install = await _acquirer.InstallAsync(
                ctx.LocalDataDir,
                LlamaRuntimeInstaller.Component(plan.Runtime),
                plan.Model,
                progress: null,
                linked.Token);
            ctx.LocalAiModelInstall = install;
            string action = install.Disposition == HuggingFaceModelInstallDisposition.ReusedVerified
                ? "Verified existing"
                : "Downloaded";
            return StepResult.Ok($"{action} {plan.Model.DisplayName} from its pinned Hugging Face revision.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            return StepResult.Fail("The Hugging Face model download timed out.", ex);
        }
        catch (Exception ex) when (
            ex is HuggingFaceModelInstallException
            or IOException
            or UnauthorizedAccessException
            or HttpRequestException)
        {
            return StepResult.Fail($"Hugging Face model installation failed: {ex.Message}", ex);
        }
    }

    public override Task RollbackAsync(SetupContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (ctx.LocalAiModelInstall is { } install)
        {
            _acquirer.RemoveInstalledModel(ctx.LocalDataDir, install);
            ctx.LocalAiModelInstall = null;
        }

        return Task.CompletedTask;
    }
}

/// <summary>Persists one immutable ownership and qualification receipt.</summary>
public sealed class PersistLocalAiManifestStep : SetupStep
{
    public override string Id => "persist-local-ai-manifest";
    public override string DisplayName => "Recording Local AI installation";
    public override bool CanRetry => false;
    public override RetryPolicy Retry => RetryPolicy.None;

    public override bool CanSkip(SetupContext ctx) => !ctx.Config.LocalAi.Enabled;

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        if (ctx.LocalAiEligibility?.Plan is not { } plan ||
            ctx.LocalAiEligibility.SelectedGpu is not { StableId: { Length: > 0 } gpuId } ||
            ctx.LocalAiPort is not > 0 ||
            ctx.LocalAiRuntimeInstall is not { } runtimeInstall ||
            ctx.LocalAiModelInstall is not { } modelInstall)
        {
            return StepResult.Terminal(
                "The Local AI installation receipt requires completed hardware, runtime, and model steps.");
        }

        if (plan.Model.Weights.Source is not HuggingFaceRevisionSource modelSource)
            return StepResult.Terminal("The selected Local AI model does not have immutable Hugging Face provenance.");

        var paths = new LocalAiPaths(ctx.LocalDataDir);
        if (File.Exists(paths.ManifestPath))
            return StepResult.Terminal("A managed Local AI installation receipt already exists.");

        ImmutableArray<LocalAiAssetReceipt> runtimeAssets;
        try
        {
            runtimeAssets = BuildRuntimeReceipts(plan.Runtime, runtimeInstall);
        }
        catch (InvalidDataException ex)
        {
            return StepResult.Terminal(ex.Message, ex);
        }

        var manifest = new LocalAiInstallManifest
        {
            EngineVersion = LlamaRuntimeCatalog.ReleaseTag,
            Architecture = plan.Runtime.Architecture switch
            {
                Architecture.X64 => "x64",
                Architecture.Arm64 => "arm64",
                _ => throw new InvalidDataException("The selected Local AI runtime architecture is unsupported."),
            },
            HardwareProfileId = plan.HardwareProfile.Id,
            RuntimeId = plan.Runtime.Id,
            ModelCatalogId = plan.Model.Id,
            SelectedGpuId = gpuId,
            ExecutablePath = Path.GetRelativePath(paths.RootDirectory, runtimeInstall.ExecutablePath),
            RuntimeAssets = runtimeAssets,
            ModelPath = Path.GetRelativePath(paths.RootDirectory, modelInstall.ModelPath),
            ModelId = $"{modelSource.RepositoryId}@{modelSource.RevisionSha}",
            ModelAlias = plan.Model.Id,
            ModelAsset = new LocalAiAssetReceipt
            {
                FileName = Path.GetFileName(plan.Model.Weights.RelativePath),
                SourceUrl = plan.Model.Weights.DownloadUri.AbsoluteUri,
                SizeBytes = plan.Model.Weights.SizeBytes,
                Sha256 = plan.Model.Weights.Sha256.Value,
            },
            Endpoint = $"http://127.0.0.1:{ctx.LocalAiPort.Value}/v1",
            ContextLength = plan.Model.Recipe.ContextTokens,
        };

        var store = new LocalAiManifestStore(paths);
        try
        {
            await store.SaveAsync(manifest, ct);
            ctx.LocalAiResolvedInstall = store.ResolveAndValidate(manifest);
            ctx.LocalAiManifestCreatedThisRun = true;
            return StepResult.Ok("Recorded the verified llama-server and Hugging Face installation.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return StepResult.Fail($"The Local AI installation receipt could not be saved: {ex.Message}", ex);
        }
    }

    public override async Task RollbackAsync(SetupContext ctx, CancellationToken ct)
    {
        if (!ctx.LocalAiManifestCreatedThisRun)
            return;

        await new LocalAiManifestStore(new LocalAiPaths(ctx.LocalDataDir)).DeleteAsync(ct);
        ctx.LocalAiResolvedInstall = null;
        ctx.LocalAiManifestCreatedThisRun = false;
    }

    private static ImmutableArray<LocalAiAssetReceipt> BuildRuntimeReceipts(
        LlamaRuntimeVariant runtime,
        LlamaRuntimeInstallResult install)
    {
        if (install.VerifiedArchives.Count != runtime.Artifacts.Count)
            throw new InvalidDataException("The installed llama-server archive receipt set is incomplete.");

        var receipts = ImmutableArray.CreateBuilder<LocalAiAssetReceipt>(runtime.Artifacts.Count);
        foreach (PinnedArtifact artifact in runtime.Artifacts)
        {
            string fileName = Path.GetFileName(artifact.RelativePath);
            LocalAiVerifiedArchive verified = install.VerifiedArchives.SingleOrDefault(
                candidate => string.Equals(candidate.FileName, fileName, StringComparison.Ordinal))
                ?? throw new InvalidDataException(
                    $"The installed llama-server archive receipt for '{fileName}' is missing.");
            if (verified.SizeBytes != artifact.SizeBytes ||
                !string.Equals(verified.Sha256, artifact.Sha256.Value, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"The installed llama-server archive receipt for '{fileName}' does not match its pin.");
            }

            receipts.Add(new LocalAiAssetReceipt
            {
                FileName = fileName,
                SourceUrl = artifact.DownloadUri.AbsoluteUri,
                SizeBytes = verified.SizeBytes,
                Sha256 = verified.Sha256,
            });
        }

        return receipts.MoveToImmutable();
    }
}

/// <summary>Starts the companion-owned llama-server router without preloading a model.</summary>
public sealed class StartLocalAiRuntimeStep : SetupStep
{
    private readonly Func<SetupContext, ILocalAiRuntime> _runtimeFactory;

    public StartLocalAiRuntimeStep()
        : this(CreateRuntime)
    {
    }

    internal StartLocalAiRuntimeStep(Func<SetupContext, ILocalAiRuntime> runtimeFactory) =>
        _runtimeFactory = runtimeFactory ?? throw new ArgumentNullException(nameof(runtimeFactory));

    public override string Id => "start-local-ai-runtime";
    public override string DisplayName => "Starting llama-server router";
    public override bool CanRetry => false;
    public override RetryPolicy Retry => RetryPolicy.None;

    public override bool CanSkip(SetupContext ctx) => !ctx.Config.LocalAi.Enabled;

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        if (ctx.LocalAiResolvedInstall is null || !ctx.LocalAiManifestCreatedThisRun)
            return StepResult.Terminal("llama-server startup requires a verified installation receipt.");
        if (ctx.LocalAiRuntime is not null)
            return StepResult.Terminal("A Local AI runtime is already attached to this setup transaction.");

        ILocalAiRuntime runtime = _runtimeFactory(ctx);
        ctx.LocalAiRuntime = runtime;
        try
        {
            LocalAiRuntimeSnapshot snapshot = await runtime.EnsureStartedAsync(ct);
            if (snapshot.State != LocalAiRuntimeState.Healthy ||
                snapshot.Ownership != LocalAiOwnership.CompanionManaged ||
                snapshot.ProcessId is null ||
                snapshot.ModelEvidence.State != LocalAiModelAvailabilityState.Verified)
            {
                await DisposeRuntimeAsync(ctx);
                return StepResult.Fail(
                    snapshot.Detail ?? "The managed llama-server router did not become healthy.");
            }

            return StepResult.Ok(
                "The companion-owned llama-server router is healthy. The model remains unloaded until the first request.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await DisposeRuntimeAsync(ctx);
            throw;
        }
        catch (Exception ex)
        {
            await DisposeRuntimeAsync(ctx);
            return StepResult.Fail($"llama-server startup failed: {ex.Message}", ex);
        }
    }

    public override Task RollbackAsync(SetupContext ctx, CancellationToken ct) =>
        DisposeRuntimeAsync(ctx).AsTask();

    private static ILocalAiRuntime CreateRuntime(SetupContext ctx)
    {
        LocalAiResolvedInstall install = ctx.LocalAiResolvedInstall
            ?? throw new InvalidOperationException("The Local AI installation receipt is unavailable.");
        return new LlamaServerRuntimeService(new LlamaServerRuntimeOptions
        {
            Paths = new LocalAiPaths(ctx.LocalDataDir),
            InitialEndpoint = install.Endpoint,
            StartupTimeout = TimeSpan.FromSeconds(ctx.Config.LocalAi.HealthTimeoutSeconds),
        });
    }

    private static async ValueTask DisposeRuntimeAsync(SetupContext ctx)
    {
        if (ctx.LocalAiRuntime is null)
            return;

        ILocalAiRuntime runtime = ctx.LocalAiRuntime;
        ctx.LocalAiRuntime = null;
        await runtime.DisposeAsync();
    }
}
