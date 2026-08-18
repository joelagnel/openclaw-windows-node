using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenClaw.SetupEngine;

internal static partial class LocalAiSetupPolicy
{
    public static string? Validate(LocalAiConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!config.Enabled)
            return null;
        if (!string.Equals(config.Engine, LocalAiConfig.DefaultEngine, StringComparison.OrdinalIgnoreCase))
            return "The first Local AI release supports only Ollama.";
        if (!string.Equals(config.Version, OllamaReleasePolicy.RecommendedVersion, StringComparison.Ordinal))
            return $"Local AI requires qualified Ollama version {OllamaReleasePolicy.RecommendedVersion}.";
        if (!Uri.TryCreate(config.Endpoint, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != Uri.UriSchemeHttp ||
            !string.Equals(endpoint.Host, "127.0.0.1", StringComparison.Ordinal) ||
            endpoint.Port != 11434 ||
            endpoint.AbsolutePath is not ("" or "/") ||
            !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query) ||
            !string.IsNullOrEmpty(endpoint.Fragment))
        {
            return "Managed Ollama must use the loopback endpoint http://127.0.0.1:11434.";
        }
        if (!ModelTagPattern().IsMatch(config.Model))
            return "The Local AI model tag contains unsupported characters.";
        if (!string.Equals(config.Model, LocalAiConfig.DefaultModel, StringComparison.Ordinal))
            return $"The first Local AI release requires model {LocalAiConfig.DefaultModel}.";
        if (config.ModelDownloadSizeBytes != LocalAiConfig.DefaultModelDownloadSizeBytes)
            return $"The qualified Local AI model size is {LocalAiConfig.DefaultModelDownloadSizeBytes} bytes.";
        if (config.ContextWindow != 262_144 || config.MaxTokens != 8_192)
            return "The qualified Local AI context is 262144 with 8192 maximum output tokens.";
        if (config.ProviderTimeoutSeconds <= 0 || config.HealthTimeoutSeconds <= 0 || config.PullTimeoutSeconds <= 0)
            return "The Local AI timeouts must be positive.";
        if (!string.Equals(config.KvCacheType, "f16", StringComparison.OrdinalIgnoreCase))
            return "The first Local AI release requires FP16 KV cache.";
        if (!config.FlashAttention || config.NumParallel != 1 || config.MaxLoadedModels != 1)
            return "The first Local AI release requires flash attention, one parallel request, and one loaded model.";
        if (!config.Reasoning ||
            !string.Equals(config.KeepAlive, "10m", StringComparison.Ordinal) ||
            !string.Equals(config.LlmLibrary, "cuda_v13", StringComparison.Ordinal))
        {
            return "The Local AI recipe settings do not match the qualified release.";
        }
        return null;
    }

    [GeneratedRegex("^[A-Za-z0-9._-]+(?::[A-Za-z0-9._-]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex ModelTagPattern();
}

internal static class LocalAiGatewayConfigBuilder
{
    public static string BuildBatchJson(LocalAiConfig config)
    {
        if (LocalAiSetupPolicy.Validate(config) is { } error)
            throw new ArgumentException(error, nameof(config));

        var provider = new
        {
            baseUrl = config.Endpoint.TrimEnd('/'),
            api = "ollama",
            apiKey = "ollama-local",
            timeoutSeconds = config.ProviderTimeoutSeconds,
            models = new[]
            {
                new
                {
                    id = config.Model,
                    name = config.Model,
                    reasoning = config.Reasoning,
                    input = new[] { "text", "image" },
                    cost = new { input = 0, output = 0, cacheRead = 0, cacheWrite = 0 },
                    contextWindow = config.ContextWindow,
                    contextTokens = config.ContextWindow,
                    maxTokens = config.MaxTokens,
                    @params = new { num_ctx = config.ContextWindow },
                    compat = new { supportsTools = true, supportsUsageInStreaming = true },
                },
            },
        };
        var operations = new object[]
        {
            new { path = "models.providers.ollama", value = provider },
            new { path = "agents.defaults.model.primary", value = $"ollama/{config.Model}" },
        };
        return JsonSerializer.Serialize(operations);
    }
}

public sealed class ConfigureLocalAiGatewayStep : SetupStep
{
    internal const string StepId = "configure-local-ai-gateway";
    private const string BatchEnvironmentVariable = "OPENCLAW_LOCAL_AI_BATCH_B64";

    public override string Id => StepId;
    public override string DisplayName => "Connect gateway to Local AI";
    public override bool CanSkip(SetupContext ctx) => !ctx.Config.LocalAi.Enabled;

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        if (LocalAiSetupPolicy.Validate(ctx.Config.LocalAi) is { } error)
            return StepResult.Terminal(error);

        var batchJson = LocalAiGatewayConfigBuilder.BuildBatchJson(ctx.Config.LocalAi);
        var environment = new Dictionary<string, string>
        {
            [BatchEnvironmentVariable] = Convert.ToBase64String(Encoding.UTF8.GetBytes(batchJson)),
        };
        var script = $$"""
            set -e
            {{ctx.WslPathPrefix}}
            batch_file="$(mktemp)"
            trap 'rm -f "$batch_file"' EXIT
            printf '%s' "$OPENCLAW_LOCAL_AI_BATCH_B64" | base64 -d > "$batch_file"
            openclaw config set --batch-file "$batch_file" --dry-run
            openclaw config set --batch-file "$batch_file"
            echo "LOCAL_AI_GATEWAY_CONFIGURED"
            """;
        var result = await ctx.Commands.RunInWslAsync(
            ctx.DistroName!,
            script,
            TimeSpan.FromMinutes(2),
            environment,
            ct,
            user: ctx.Config.Wsl.User,
            inputViaStdin: true);

        if (result.ExitCode != 0 || !result.Stdout.Contains("LOCAL_AI_GATEWAY_CONFIGURED", StringComparison.Ordinal))
        {
            return StepResult.Fail(
                result.TimedOut
                    ? "Local AI gateway configuration timed out."
                    : $"Local AI gateway configuration failed (exit {result.ExitCode}): {result.Stderr}");
        }

        return StepResult.Ok("Gateway configured to use local Ollama");
    }
}
