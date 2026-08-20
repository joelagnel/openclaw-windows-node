using OpenClaw.Connection;
using OpenClaw.Connection.LocalAi;
using OpenClaw.Shared;
using System.Text;
using System.Text.Json;

namespace OpenClawTray.Services;

/// <summary>
/// Keeps the app-owned WSL gateway from routing to a listener while the native
/// llama-server endpoint is absent, changing, or not owned by this companion.
/// </summary>
internal sealed class LocalAiGatewayProviderCoordinator : ILocalAiEndpointLifecycle
{
    private const string FixedPath = "/home/openclaw/.openclaw/bin:/opt/openclaw/bin:/usr/local/bin:/usr/bin:/bin";
    private const int MaximumConfigBytes = 1024 * 1024;

    private readonly IWslCommandRunner _commands;
    private readonly string _distroName;
    private readonly IOpenClawLogger _logger;

    public LocalAiGatewayProviderCoordinator(
        IWslCommandRunner commands,
        string distroName,
        IOpenClawLogger logger)
    {
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        ArgumentException.ThrowIfNullOrWhiteSpace(distroName);
        _distroName = distroName.Trim();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<LocalAiEndpointLifecycleResult> QuiesceAsync(
        LocalAiResolvedInstall install,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(install);
        ProviderCapture current = await CaptureProviderAsync(cancellationToken).ConfigureAwait(false);
        if (!current.Success)
            return Failed(current.Detail ?? "The managed Local AI provider could not be inspected.");
        if (!current.Exists)
            return LocalAiEndpointLifecycleResult.Ok();

        try
        {
            _ = LocalAiGatewayProviderDefinition.BuildProviderJson(install);
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
        {
            return Failed(ex.Message);
        }
        if (!LocalAiGatewayProviderDefinition.MatchesProviderJson(current.Json!, install))
            return Failed("The llamacpp provider was changed outside the companion; preserving it and refusing to cycle the managed endpoint.");

        WslCommandResult unset = await RunOpenClawAsync(
                ["config", "unset", LocalAiGatewayProviderDefinition.ProviderPath],
                cancellationToken)
            .ConfigureAwait(false);
        if (!unset.Success)
            return Failed("The managed llamacpp provider could not be disabled before the endpoint changed.");

        ProviderCapture verified = await CaptureProviderAsync(cancellationToken).ConfigureAwait(false);
        if (!verified.Success || verified.Exists)
            return Failed("The managed llamacpp provider remained configured after it was disabled.");
        return LocalAiEndpointLifecycleResult.Ok();
    }

    public async Task<LocalAiEndpointLifecycleResult> PublishAsync(
        LocalAiResolvedInstall install,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(install);
        string batch;
        try
        {
            batch = LocalAiGatewayProviderDefinition.BuildProviderBatchJson(install);
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
        {
            return Failed(ex.Message);
        }

        ProviderCapture current = await CaptureProviderAsync(cancellationToken).ConfigureAwait(false);
        if (!current.Success)
            return Failed(current.Detail ?? "The managed Local AI provider could not be inspected.");
        if (current.Exists)
        {
            return LocalAiGatewayProviderDefinition.MatchesProviderJson(current.Json!, install)
                ? LocalAiEndpointLifecycleResult.Ok()
                : Failed("The llamacpp provider was changed outside the companion; preserving it instead of publishing the managed endpoint.");
        }

        string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(batch));
        string script =
            $"set -e\nprintf '%s' '{encoded}' | base64 -d | openclaw config set --batch-file /dev/stdin --dry-run\n" +
            $"printf '%s' '{encoded}' | base64 -d | openclaw config set --batch-file /dev/stdin";
        WslCommandResult applied = await _commands.RunInDistroAsync(
                _distroName,
                ["/usr/bin/env", $"PATH={FixedPath}", "/bin/sh", "-c", script],
                cancellationToken)
            .ConfigureAwait(false);
        if (!applied.Success)
        {
            LocalAiEndpointLifecycleResult cleanup = await RemoveExactPublishedProviderAsync(
                    install,
                    cancellationToken)
                .ConfigureAwait(false);
            return PublicationFailed(
                "The verified Local AI endpoint could not be published to the app-owned gateway.",
                cleanup);
        }

        ProviderCapture verified = await CaptureProviderAsync(cancellationToken).ConfigureAwait(false);
        if (!verified.Success || !verified.Exists ||
            !LocalAiGatewayProviderDefinition.MatchesProviderJson(verified.Json!, install))
        {
            LocalAiEndpointLifecycleResult cleanup = await RemoveExactPublishedProviderAsync(
                    install,
                    cancellationToken)
                .ConfigureAwait(false);
            return PublicationFailed(
                "The app-owned gateway did not retain the verified Local AI endpoint.",
                cleanup);
        }
        return LocalAiEndpointLifecycleResult.Ok();
    }

    private async Task<LocalAiEndpointLifecycleResult> RemoveExactPublishedProviderAsync(
        LocalAiResolvedInstall install,
        CancellationToken cancellationToken)
    {
        ProviderCapture current = await CaptureProviderAsync(cancellationToken).ConfigureAwait(false);
        if (!current.Success)
        {
            return LocalAiEndpointLifecycleResult.Failed(
                "The just-written provider could not be inspected for safe cleanup.");
        }
        if (!current.Exists)
            return LocalAiEndpointLifecycleResult.Ok();
        if (!LocalAiGatewayProviderDefinition.MatchesProviderJson(current.Json!, install))
        {
            return LocalAiEndpointLifecycleResult.Failed(
                "The provider changed during publication, so cleanup preserved the unproven value.");
        }

        WslCommandResult unset = await RunOpenClawAsync(
                ["config", "unset", LocalAiGatewayProviderDefinition.ProviderPath],
                cancellationToken)
            .ConfigureAwait(false);
        if (!unset.Success)
        {
            return LocalAiEndpointLifecycleResult.Failed(
                "The just-written provider could not be removed after publication failed.");
        }

        ProviderCapture verified = await CaptureProviderAsync(cancellationToken).ConfigureAwait(false);
        return verified.Success && !verified.Exists
            ? LocalAiEndpointLifecycleResult.Ok()
            : LocalAiEndpointLifecycleResult.Failed(
                "The just-written provider remained configured after cleanup.");
    }

    private LocalAiEndpointLifecycleResult PublicationFailed(
        string detail,
        LocalAiEndpointLifecycleResult cleanup) => cleanup.Success
            ? Failed($"{detail} The just-written provider was removed.")
            : Failed($"{detail} Cleanup also failed: {cleanup.Detail}");

    private async Task<ProviderCapture> CaptureProviderAsync(CancellationToken cancellationToken)
    {
        WslCommandResult direct = await RunOpenClawAsync(
                ["config", "get", LocalAiGatewayProviderDefinition.ProviderPath, "--json"],
                cancellationToken)
            .ConfigureAwait(false);
        if (direct.Success)
            return ParseProvider(direct.StandardOutput);
        // Validated OpenClaw releases report an absent key with this exact error.
        // Every other CLI/distro failure is operationally ambiguous and fails closed.
        string missing = $"Config path not found: {LocalAiGatewayProviderDefinition.ProviderPath}";
        return direct.StandardError.Contains(missing, StringComparison.Ordinal)
            ? new(true, false, null, null)
            : new(false, false, null, "The app-owned gateway provider configuration could not be read.");
    }

    private static ProviderCapture ParseProvider(string value)
    {
        try
        {
            using JsonDocument document = ParseBounded(value);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return new(false, false, null, "The managed llamacpp provider has an invalid shape.");
            return new(true, true, document.RootElement.GetRawText(), null);
        }
        catch (JsonException)
        {
            return new(false, false, null, "The managed llamacpp provider is not valid JSON.");
        }
    }

    private static JsonDocument ParseBounded(string value)
    {
        if (Encoding.UTF8.GetByteCount(value) > MaximumConfigBytes)
            throw new JsonException("The gateway configuration is too large.");
        return JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 32 });
    }

    private Task<WslCommandResult> RunOpenClawAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var command = new List<string>(arguments.Count + 3)
        {
            "/usr/bin/env",
            $"PATH={FixedPath}",
            "openclaw",
        };
        command.AddRange(arguments);
        return _commands.RunInDistroAsync(_distroName, command, cancellationToken);
    }

    private LocalAiEndpointLifecycleResult Failed(string detail)
    {
        _logger.Warn(detail);
        return LocalAiEndpointLifecycleResult.Failed(detail);
    }

    private sealed record ProviderCapture(bool Success, bool Exists, string? Json, string? Detail);
}
