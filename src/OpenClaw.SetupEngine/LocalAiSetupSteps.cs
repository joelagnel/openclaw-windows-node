using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using OpenClaw.Connection.LocalAi;

namespace OpenClaw.SetupEngine;

internal delegate Task<LocalAiArtifactInstallResult> LocalAiArtifactAcquire(
    SetupContext context,
    Architecture architecture,
    IProgress<LocalAiArtifactInstallProgress> progress,
    CancellationToken cancellationToken);

internal delegate Task LocalAiManifestSave(
    LocalAiManifestStore store,
    LocalAiInstallManifest manifest,
    CancellationToken cancellationToken);

internal sealed class OllamaApiClientLease(IOllamaApiClient client, IDisposable? owner = null) : IDisposable
{
    public IOllamaApiClient Client { get; } = client ?? throw new ArgumentNullException(nameof(client));
    public void Dispose() => owner?.Dispose();
}

internal static class LocalAiSetupRuntimeFactory
{
    internal const int ModelProgressIntervalBytes = 4 * 1024 * 1024;

    public static ILocalAiRuntime Create(SetupContext context)
    {
        var config = context.Config.LocalAi;
        return new OllamaRuntimeService(
            new OllamaRuntimeOptions
            {
                Paths = new LocalAiPaths(context.LocalDataDir),
                Endpoint = new Uri(config.Endpoint),
                StartupTimeout = TimeSpan.FromSeconds(config.HealthTimeoutSeconds),
                MaxRestartAttempts = 0,
                ContextLength = config.ContextWindow,
                FlashAttention = config.FlashAttention,
                KvCacheType = config.KvCacheType,
                NumParallel = config.NumParallel,
                MaxLoadedModels = config.MaxLoadedModels,
                KeepAlive = TimeSpan.FromMinutes(10),
                LlmLibrary = config.LlmLibrary,
            },
            new SetupOpenClawLogger(context.Logger));
    }

    public static OllamaApiClientLease CreateApiClient(SetupContext context)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(Math.Min(10, context.Config.LocalAi.HealthTimeoutSeconds)),
        };
        var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        return new OllamaApiClientLease(
            new OllamaApiClient(client, new Uri(context.Config.LocalAi.Endpoint)),
            client);
    }
}

public sealed class AcquireLocalAiEngineStep : SetupStep
{
    internal const string StepId = "acquire-local-ai-engine";

    private readonly Func<SetupContext, ILocalAiRuntime> _runtimeFactory;
    private readonly LocalAiArtifactAcquire _acquire;
    private readonly Func<Architecture> _architectureProvider;
    private readonly LocalAiManifestSave _saveManifest;

    public AcquireLocalAiEngineStep()
        : this(
            LocalAiSetupRuntimeFactory.Create,
            AcquireOfficialArtifactAsync,
            () => RuntimeInformation.OSArchitecture)
    {
    }

    internal AcquireLocalAiEngineStep(
        Func<SetupContext, ILocalAiRuntime> runtimeFactory,
        LocalAiArtifactAcquire acquire,
        Func<Architecture> architectureProvider,
        LocalAiManifestSave? saveManifest = null)
    {
        _runtimeFactory = runtimeFactory ?? throw new ArgumentNullException(nameof(runtimeFactory));
        _acquire = acquire ?? throw new ArgumentNullException(nameof(acquire));
        _architectureProvider = architectureProvider ?? throw new ArgumentNullException(nameof(architectureProvider));
        _saveManifest = saveManifest ?? SaveManifestAsync;
    }

    public override string Id => StepId;
    public override string DisplayName => "Acquire native Ollama";
    public override bool CanSkip(SetupContext ctx) => !ctx.Config.LocalAi.Enabled;
    public override bool CanRetry => false;

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        if (LocalAiSetupPolicy.Validate(ctx.Config.LocalAi) is { } validationError)
            return StepResult.Terminal(validationError);

        var architecture = _architectureProvider();
        OllamaReleaseArtifact artifact;
        try
        {
            artifact = OllamaReleasePolicy.Resolve(architecture);
        }
        catch (PlatformNotSupportedException ex)
        {
            return StepResult.Terminal(ex.Message, ex);
        }

        await using (var runtime = _runtimeFactory(ctx))
        {
            var snapshot = await runtime.EnsureStartedAsync(ct).ConfigureAwait(false);
            ctx.LocalAiOwnership = snapshot.Ownership;
            ctx.LocalAiEngineVersion = snapshot.EngineVersion;

            if (snapshot.State == LocalAiRuntimeState.Healthy && snapshot.Ownership == LocalAiOwnership.External)
                return StepResult.Ok("Using existing healthy external Ollama");

            if (snapshot.State == LocalAiRuntimeState.Healthy && snapshot.Ownership == LocalAiOwnership.Managed)
            {
                var existingError = await ValidateExistingManifestAsync(ctx, artifact, ct).ConfigureAwait(false);
                return existingError is null
                    ? StepResult.Ok("Qualified managed Ollama is already installed")
                    : StepResult.Terminal(existingError);
            }

            if (snapshot.State is LocalAiRuntimeState.Conflict or LocalAiRuntimeState.Failed)
            {
                return StepResult.Terminal(
                    snapshot.Detail ?? "The Ollama endpoint is occupied or could not be safely inspected.");
            }

            if (snapshot.State is not (LocalAiRuntimeState.NotInstalled or LocalAiRuntimeState.Stopped))
                return StepResult.Terminal(snapshot.Detail ?? $"Ollama entered unexpected state {snapshot.State}.");
        }

        var paths = new LocalAiPaths(ctx.LocalDataDir);
        var manifestStore = new LocalAiManifestStore(paths);
        LocalAiResolvedInstall? existing;
        try
        {
            existing = await manifestStore.LoadAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return StepResult.Terminal($"The managed Ollama manifest is invalid: {ex.Message}", ex);
        }

        if (existing is not null)
            return StepResult.Terminal("A managed Ollama manifest exists but its runtime is not healthy.");

        LocalAiArtifactInstallResult? installed = null;
        var modelsDirectoryCreated = false;
        try
        {
            var progress = new ProgressAdapter<LocalAiArtifactInstallProgress>(value =>
                ctx.ReportDetailProgress(new(
                    "artifact",
                    value.Phase.ToString().ToLowerInvariant(),
                    value.Completed,
                    value.Total,
                    value.Unit switch
                    {
                        LocalAiArtifactProgressUnit.Bytes => SetupDetailProgressUnit.Bytes,
                        LocalAiArtifactProgressUnit.Entries => SetupDetailProgressUnit.Entries,
                        _ => SetupDetailProgressUnit.None,
                    })));
            installed = await _acquire(ctx, architecture, progress, ct).ConfigureAwait(false);

            if (!string.Equals(installed.Version, artifact.Version, StringComparison.Ordinal) ||
                !string.Equals(installed.RuntimeIdentifier, artifact.RuntimeIdentifier, StringComparison.Ordinal) ||
                !string.Equals(installed.ModelsDirectory, paths.ModelsDirectory, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The acquired Ollama artifact does not match the qualified release.");
            }

            modelsDirectoryCreated = !Directory.Exists(paths.ModelsDirectory);
            Directory.CreateDirectory(paths.ModelsDirectory);
            var manifest = new LocalAiInstallManifest
            {
                EngineVersion = artifact.Version,
                Architecture = ToManifestArchitecture(architecture),
                ExecutablePath = Path.GetRelativePath(paths.RootDirectory, installed.EngineExecutablePath),
                ModelsPath = Path.GetRelativePath(paths.RootDirectory, paths.ModelsDirectory),
                ModelTag = ctx.Config.LocalAi.Model,
                Endpoint = ctx.Config.LocalAi.Endpoint,
                ContextLength = ctx.Config.LocalAi.ContextWindow,
            };
            await _saveManifest(manifestStore, manifest, ct).ConfigureAwait(false);

            ctx.LocalAiOwnership = LocalAiOwnership.Managed;
            ctx.LocalAiEngineVersion = artifact.Version;
            ctx.CreatedManagedLocalAiInstallThisRun = true;
            return StepResult.Ok($"Installed managed Ollama {artifact.Version}");
        }
        catch (OperationCanceledException)
        {
            CleanupFailedPromotion(ctx, installed, modelsDirectoryCreated, paths.ModelsDirectory);
            throw;
        }
        catch (Exception ex)
        {
            CleanupFailedPromotion(ctx, installed, modelsDirectoryCreated, paths.ModelsDirectory);
            if (ex is IOException or UnauthorizedAccessException or LocalAiArtifactInstallException or HttpRequestException)
                return StepResult.Fail($"Managed Ollama acquisition failed: {ex.Message}", ex);
            throw;
        }
    }

    private static Task SaveManifestAsync(
        LocalAiManifestStore store,
        LocalAiInstallManifest manifest,
        CancellationToken cancellationToken)
        => store.SaveAsync(manifest, cancellationToken);

    private static void CleanupFailedPromotion(
        SetupContext context,
        LocalAiArtifactInstallResult? installed,
        bool modelsDirectoryCreated,
        string modelsDirectory)
    {
        TryCleanup(() =>
        {
            if (installed?.RollbackDirectory is { } rollbackDirectory)
                LocalAiManagedStorage.TryDeleteOwnedDirectory(context, rollbackDirectory);
        }, "promoted Ollama engine");

        if (modelsDirectoryCreated)
        {
            TryCleanup(
                () => LocalAiManagedStorage.TryDeleteOwnedDirectory(context, modelsDirectory),
                "new managed model directory");
        }

        void TryCleanup(Action cleanup, string description)
        {
            try
            {
                cleanup();
            }
            catch (Exception cleanupError)
            {
                context.Logger.Warn($"Could not clean the {description} after acquisition failed: {cleanupError.Message}");
            }
        }
    }

    public override async Task RollbackAsync(SetupContext ctx, CancellationToken ct)
    {
        if (ctx.LocalAiOwnership == LocalAiOwnership.External)
            return;
        if (!ctx.CreatedManagedLocalAiInstallThisRun && !ctx.Config.ConfirmDestructive)
            return;

        await using var runtime = _runtimeFactory(ctx);
        var snapshot = await runtime.RefreshAsync(ct).ConfigureAwait(false);
        if (snapshot.Ownership == LocalAiOwnership.External ||
            snapshot.State is LocalAiRuntimeState.Healthy or LocalAiRuntimeState.Conflict or LocalAiRuntimeState.Failed)
        {
            ctx.Logger.Warn("Managed Local AI cleanup was skipped because the endpoint is active or its ownership is uncertain.");
            return;
        }

        await LocalAiManagedStorage.DeleteInstallFromManifestAsync(ctx, ct).ConfigureAwait(false);
        ctx.CreatedManagedLocalAiInstallThisRun = false;
        ctx.DownloadedManagedLocalAiModelThisRun = false;
    }

    private static async Task<string?> ValidateExistingManifestAsync(
        SetupContext context,
        OllamaReleaseArtifact artifact,
        CancellationToken cancellationToken)
    {
        try
        {
            var paths = new LocalAiPaths(context.LocalDataDir);
            var install = await new LocalAiManifestStore(paths).LoadAsync(cancellationToken).ConfigureAwait(false);
            if (install is null)
                return "Managed Ollama is healthy without its ownership manifest.";
            if (!string.Equals(install.Manifest.EngineVersion, artifact.Version, StringComparison.Ordinal) ||
                !string.Equals(install.Manifest.Architecture, ToManifestArchitecture(artifact.Architecture), StringComparison.Ordinal) ||
                !string.Equals(install.Manifest.ModelTag, context.Config.LocalAi.Model, StringComparison.Ordinal) ||
                install.Manifest.ContextLength != context.Config.LocalAi.ContextWindow ||
                !string.Equals(install.ModelsPath, paths.ModelsDirectory, StringComparison.OrdinalIgnoreCase))
            {
                return "The managed Ollama manifest does not match the qualified Local AI recipe.";
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"The managed Ollama manifest is invalid: {ex.Message}";
        }
    }

    private static async Task<LocalAiArtifactInstallResult> AcquireOfficialArtifactAsync(
        SetupContext context,
        Architecture architecture,
        IProgress<LocalAiArtifactInstallProgress> progress,
        CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var installer = new LocalAiArtifactInstaller(client);
        return await installer.InstallAsync(
            context.LocalDataDir,
            architecture,
            progress,
            cancellationToken).ConfigureAwait(false);
    }

    private static string ToManifestArchitecture(Architecture architecture) => architecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        _ => throw new PlatformNotSupportedException($"Unsupported Local AI architecture {architecture}."),
    };
}

public sealed class DownloadLocalAiModelStep : SetupStep
{
    internal const string StepId = "download-local-ai-model";
    private readonly Func<SetupContext, ILocalAiRuntime> _runtimeFactory;
    private readonly Func<SetupContext, OllamaApiClientLease> _apiClientFactory;

    public DownloadLocalAiModelStep()
        : this(LocalAiSetupRuntimeFactory.Create, LocalAiSetupRuntimeFactory.CreateApiClient)
    {
    }

    internal DownloadLocalAiModelStep(
        Func<SetupContext, ILocalAiRuntime> runtimeFactory,
        Func<SetupContext, OllamaApiClientLease> apiClientFactory)
    {
        _runtimeFactory = runtimeFactory ?? throw new ArgumentNullException(nameof(runtimeFactory));
        _apiClientFactory = apiClientFactory ?? throw new ArgumentNullException(nameof(apiClientFactory));
    }

    public override string Id => StepId;
    public override string DisplayName => "Download Local AI model";
    public override bool CanSkip(SetupContext ctx) => !ctx.Config.LocalAi.Enabled;
    public override bool CanRetry => false;

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        if (LocalAiSetupPolicy.Validate(ctx.Config.LocalAi) is { } validationError)
            return StepResult.Terminal(validationError);

        ILocalAiRuntime? runtime = null;
        OllamaApiClientLease? apiLease = null;
        var managedPullStarted = false;
        var completed = false;
        CancellationTokenSource? timeout = null;
        try
        {
            runtime = _runtimeFactory(ctx);
            var snapshot = await runtime.EnsureStartedAsync(ct).ConfigureAwait(false);
            ctx.LocalAiOwnership = snapshot.Ownership;
            ctx.LocalAiEngineVersion = snapshot.EngineVersion;
            if (snapshot.State != LocalAiRuntimeState.Healthy ||
                snapshot.Ownership is not (LocalAiOwnership.Managed or LocalAiOwnership.External))
            {
                return StepResult.Terminal(snapshot.Detail ?? "Ollama did not become healthy for model download.");
            }

            if (snapshot.Ownership == LocalAiOwnership.Managed)
            {
                var manifestError = await ValidateManagedModelStoreAsync(ctx, ct).ConfigureAwait(false);
                if (manifestError is not null)
                    return StepResult.Terminal(manifestError);
            }

            apiLease = _apiClientFactory(ctx);
            timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(ctx.Config.LocalAi.PullTimeoutSeconds));
            var operationToken = timeout.Token;
            var models = await apiLease.Client.ListModelsAsync(operationToken).ConfigureAwait(false);
            if (HasExactModel(models, ctx.Config.LocalAi.Model))
            {
                completed = true;
                ctx.ReportDetailProgress(new(
                    "model",
                    "already present",
                    ctx.Config.LocalAi.ModelDownloadSizeBytes,
                    ctx.Config.LocalAi.ModelDownloadSizeBytes,
                    SetupDetailProgressUnit.Bytes));
                return StepResult.Skip($"Exact model {ctx.Config.LocalAi.Model} is already present");
            }

            managedPullStarted = snapshot.Ownership == LocalAiOwnership.Managed;
            var lastReportedBytes = 0L;
            ctx.ReportDetailProgress(new(
                "model",
                "starting",
                0,
                ctx.Config.LocalAi.ModelDownloadSizeBytes,
                SetupDetailProgressUnit.Bytes));
            var progress = new ProgressAdapter<OllamaPullProgress>(value =>
            {
                if (!value.IsComplete && value.TransferredBytes - lastReportedBytes < LocalAiSetupRuntimeFactory.ModelProgressIntervalBytes)
                    return;
                ctx.ReportDetailProgress(new(
                    "model",
                    value.Status,
                    value.TransferredBytes,
                    value.ExpectedBytes,
                    SetupDetailProgressUnit.Bytes));
                lastReportedBytes = value.TransferredBytes;
            });
            await apiLease.Client.PullModelAsync(
                ctx.Config.LocalAi.Model,
                ctx.Config.LocalAi.ModelDownloadSizeBytes,
                progress,
                operationToken).ConfigureAwait(false);
            ctx.DownloadedManagedLocalAiModelThisRun = snapshot.Ownership == LocalAiOwnership.Managed;

            var verifiedModels = await apiLease.Client.ListModelsAsync(operationToken).ConfigureAwait(false);
            if (!HasExactModel(verifiedModels, ctx.Config.LocalAi.Model))
                return StepResult.Fail("Ollama completed the pull but did not report the exact qualified model tag.");

            completed = true;
            return StepResult.Ok($"Downloaded {ctx.Config.LocalAi.Model}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex) when (timeout?.IsCancellationRequested == true)
        {
            return StepResult.Fail(
                $"Local AI model download timed out after {ctx.Config.LocalAi.PullTimeoutSeconds} seconds.",
                ex);
        }
        catch (Exception ex) when (ex is OllamaApiException or IOException or UnauthorizedAccessException or HttpRequestException)
        {
            return StepResult.Fail($"Local AI model download failed: {ex.Message}", ex);
        }
        finally
        {
            timeout?.Dispose();
            apiLease?.Dispose();
            if (runtime is not null)
            {
                try { await runtime.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) { ctx.Logger.Warn($"Could not dispose the setup Ollama runtime: {ex.Message}"); }
            }

            if (!completed && managedPullStarted)
                await LocalAiManagedStorage.TryDeletePartialModelDataAsync(ctx).ConfigureAwait(false);
        }
    }

    public override async Task RollbackAsync(SetupContext ctx, CancellationToken ct)
    {
        if (!ctx.DownloadedManagedLocalAiModelThisRun || ctx.LocalAiOwnership == LocalAiOwnership.External)
            return;

        ILocalAiRuntime? runtime = null;
        OllamaApiClientLease? apiLease = null;
        try
        {
            runtime = _runtimeFactory(ctx);
            var snapshot = await runtime.EnsureStartedAsync(ct).ConfigureAwait(false);
            if (snapshot.State != LocalAiRuntimeState.Healthy || snapshot.Ownership != LocalAiOwnership.Managed)
                return;
            apiLease = _apiClientFactory(ctx);
            await apiLease.Client.DeleteModelAsync(ctx.Config.LocalAi.Model, ct).ConfigureAwait(false);
            ctx.DownloadedManagedLocalAiModelThisRun = false;
        }
        finally
        {
            apiLease?.Dispose();
            if (runtime is not null)
                await runtime.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static bool HasExactModel(IReadOnlyList<OllamaModelInfo> models, string requiredTag)
        => models.Any(model =>
            string.Equals(model.Name, requiredTag, StringComparison.Ordinal) ||
            string.Equals(model.Model, requiredTag, StringComparison.Ordinal));

    private static async Task<string?> ValidateManagedModelStoreAsync(
        SetupContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var paths = new LocalAiPaths(context.LocalDataDir);
            var install = await new LocalAiManifestStore(paths).LoadAsync(cancellationToken).ConfigureAwait(false);
            if (install is null)
                return "Managed Ollama has no ownership manifest.";
            if (!string.Equals(install.ModelsPath, paths.ModelsDirectory, StringComparison.OrdinalIgnoreCase))
                return "Managed Ollama is not configured to use the isolated OpenClaw model store.";
            return null;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return $"The managed Ollama manifest is invalid: {ex.Message}";
        }
    }
}

public sealed class VerifyLocalAiWslStep : SetupStep
{
    internal const string StepId = "verify-local-ai-wsl";
    private const string VersionMarker = "OPENCLAW_OLLAMA_VERSION_B64=";
    private const string TagsMarker = "OPENCLAW_OLLAMA_TAGS_B64=";
    private readonly Func<SetupContext, ILocalAiRuntime> _runtimeFactory;

    public VerifyLocalAiWslStep()
        : this(LocalAiSetupRuntimeFactory.Create)
    {
    }

    internal VerifyLocalAiWslStep(Func<SetupContext, ILocalAiRuntime> runtimeFactory)
        => _runtimeFactory = runtimeFactory ?? throw new ArgumentNullException(nameof(runtimeFactory));

    public override string Id => StepId;
    public override string DisplayName => "Verify Local AI from WSL";
    public override bool CanSkip(SetupContext ctx) => !ctx.Config.LocalAi.Enabled;
    public override bool CanRetry => false;

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        if (LocalAiSetupPolicy.Validate(ctx.Config.LocalAi) is { } validationError)
            return StepResult.Terminal(validationError);

        ctx.ReportDetailProgress(new("verification", "starting", null, null, SetupDetailProgressUnit.None));
        await using var runtime = _runtimeFactory(ctx);
        var snapshot = await runtime.EnsureStartedAsync(ct).ConfigureAwait(false);
        ctx.LocalAiOwnership = snapshot.Ownership;
        ctx.LocalAiEngineVersion = snapshot.EngineVersion;
        if (snapshot.State != LocalAiRuntimeState.Healthy ||
            snapshot.Ownership is not (LocalAiOwnership.Managed or LocalAiOwnership.External))
        {
            return StepResult.Terminal(snapshot.Detail ?? "Ollama did not become healthy for WSL verification.");
        }

        var endpoint = ctx.Config.LocalAi.Endpoint.TrimEnd('/');
        var script = $$"""
            set -eu
            version_json="$(curl --fail --silent --show-error --max-time 15 '{{endpoint}}/api/version')"
            tags_json="$(curl --fail --silent --show-error --max-time 30 '{{endpoint}}/api/tags')"
            printf '{{VersionMarker}}'
            printf '%s' "$version_json" | base64 | tr -d '\n'
            printf '\n{{TagsMarker}}'
            printf '%s' "$tags_json" | base64 | tr -d '\n'
            printf '\n'
            """;
        var result = await ctx.Commands.RunInWslAsync(
            ctx.DistroName!,
            script,
            TimeSpan.FromSeconds(Math.Max(45, ctx.Config.LocalAi.HealthTimeoutSeconds)),
            ct: ct,
            user: ctx.Config.Wsl.User,
            inputViaStdin: true).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            return StepResult.Fail(
                result.TimedOut
                    ? "Local AI verification from WSL timed out."
                    : $"Local AI verification from WSL failed (exit {result.ExitCode}): {result.Stderr}");
        }

        try
        {
            var versionJson = DecodeMarker(result.Stdout, VersionMarker);
            var tagsJson = DecodeMarker(result.Stdout, TagsMarker);
            using var versionDocument = JsonDocument.Parse(versionJson);
            if (!versionDocument.RootElement.TryGetProperty("version", out var version) ||
                string.IsNullOrWhiteSpace(version.GetString()))
            {
                return StepResult.Fail("WSL reached Ollama but received no engine version.");
            }
            if (snapshot.Ownership == LocalAiOwnership.Managed &&
                !string.Equals(version.GetString(), ctx.Config.LocalAi.Version, StringComparison.Ordinal))
            {
                return StepResult.Fail(
                    $"WSL reached managed Ollama {version.GetString()}, but qualified version {ctx.Config.LocalAi.Version} is required.");
            }

            if (!TagsContainExactModel(tagsJson, ctx.Config.LocalAi.Model))
                return StepResult.Fail("WSL reached Ollama but the exact qualified model tag is missing.");

            ctx.ReportDetailProgress(new("verification", "complete", 1, 1, SetupDetailProgressUnit.None));
            return StepResult.Ok($"WSL reached Ollama {version.GetString()} and {ctx.Config.LocalAi.Model}");
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidDataException)
        {
            return StepResult.Fail($"WSL returned an invalid Ollama verification response: {ex.Message}", ex);
        }
    }

    private static string DecodeMarker(string output, string marker)
    {
        var line = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(value => value.StartsWith(marker, StringComparison.Ordinal));
        if (line is null)
            throw new InvalidDataException($"Missing verification marker {marker.TrimEnd('=')}.");
        return Encoding.UTF8.GetString(Convert.FromBase64String(line[marker.Length..]));
    }

    private static bool TagsContainExactModel(string json, string requiredTag)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
            return false;
        foreach (var model in models.EnumerateArray())
        {
            foreach (var propertyName in new[] { "name", "model" })
            {
                if (model.TryGetProperty(propertyName, out var value) &&
                    string.Equals(value.GetString(), requiredTag, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }
}

internal static class LocalAiManagedStorage
{
    public static async Task DeleteInstallFromManifestAsync(SetupContext context, CancellationToken cancellationToken)
    {
        var paths = new LocalAiPaths(context.LocalDataDir);
        RejectManifestReparsePoint(paths.ManifestPath);
        var store = new LocalAiManifestStore(paths);
        var install = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (install is null)
            return;

        var engineDirectory = Path.GetDirectoryName(install.ExecutablePath)
            ?? throw new InvalidDataException("Managed Ollama executable has no parent directory.");
        var deleteTargets = new[] { engineDirectory, install.ModelsPath }
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var target in deleteTargets)
            ValidateOwnedTree(context, target);
        foreach (var target in deleteTargets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(target))
                Directory.Delete(target, recursive: true);
        }

        await store.DeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task TryDeletePartialModelDataAsync(SetupContext context)
    {
        try
        {
            var paths = new LocalAiPaths(context.LocalDataDir);
            RejectManifestReparsePoint(paths.ManifestPath);
            var install = await new LocalAiManifestStore(paths).LoadAsync(CancellationToken.None).ConfigureAwait(false);
            if (install is null || !Directory.Exists(install.ModelsPath))
                return;

            ValidateOwnedTree(context, install.ModelsPath);
            foreach (var file in Directory.EnumerateFiles(install.ModelsPath, "*", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(file);
                if (name.Contains("-partial", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            context.Logger.Warn($"Could not clean partial managed model data: {ex.Message}");
        }
    }

    public static void TryDeleteOwnedDirectory(SetupContext context, string target)
    {
        try
        {
            ValidateOwnedTree(context, target);
            if (Directory.Exists(target))
                Directory.Delete(target, recursive: true);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            context.Logger.Warn($"Could not clean managed Local AI path '{target}': {ex.Message}");
        }
    }

    private static void ValidateOwnedTree(SetupContext context, string target)
    {
        if (!LocalAiPathPolicy.TryValidateManagedDeleteTarget(
                context.LocalDataDir,
                target,
                out _,
                out var pathError))
        {
            throw new InvalidDataException(pathError);
        }

        if (!Directory.Exists(target))
            return;
        var pending = new Stack<string>();
        pending.Push(target);
        while (pending.TryPop(out var directory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new InvalidDataException($"Managed Local AI path '{entry}' is a reparse point.");
                if (attributes.HasFlag(FileAttributes.Directory))
                    pending.Push(entry);
            }
        }
    }

    private static void RejectManifestReparsePoint(string manifestPath)
    {
        if (File.Exists(manifestPath) && File.GetAttributes(manifestPath).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("The managed Local AI manifest is a reparse point.");
    }
}

internal sealed class ProgressAdapter<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
