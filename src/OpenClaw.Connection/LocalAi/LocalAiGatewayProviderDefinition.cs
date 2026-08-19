using OpenClaw.Shared.Inference.Catalog;
using System.Text.Json;

namespace OpenClaw.Connection.LocalAi;

/// <summary>Canonical gateway configuration for the companion-owned llama.cpp provider.</summary>
public static class LocalAiGatewayProviderDefinition
{
    public const string ProviderPath = "models.providers.llamacpp";
    public const string PrimaryModelPath = "agents.defaults.model.primary";
    public const int ProviderTimeoutSeconds = 300;
    public const int MaximumOutputTokens = 8_192;

    public static string BuildProviderJson(LocalAiResolvedInstall install)
    {
        ArgumentNullException.ThrowIfNull(install);
        Uri endpoint = install.Endpoint
            ?? throw new InvalidOperationException("The verified Local AI endpoint is required.");
        LocalModelInfo model = LocalModelCatalog.Find(install.Manifest.ModelCatalogId)
            ?? throw new InvalidDataException("The managed Local AI model is no longer qualified.");
        if (!string.Equals(model.Id, install.Manifest.ModelAlias, StringComparison.Ordinal))
            throw new InvalidDataException("The managed Local AI model alias does not match the qualified catalog.");

        var value = new
        {
            baseUrl = endpoint.AbsoluteUri.TrimEnd('/'),
            api = "openai-completions",
            apiKey = "llama-local",
            timeoutSeconds = ProviderTimeoutSeconds,
            models = new[]
            {
                new
                {
                    id = install.Manifest.ModelAlias,
                    name = model.DisplayName,
                    reasoning = true,
                    input = new[] { "text" },
                    cost = new { input = 0, output = 0, cacheRead = 0, cacheWrite = 0 },
                    contextWindow = install.Manifest.ContextLength,
                    contextTokens = install.Manifest.ContextLength,
                    maxTokens = MaximumOutputTokens,
                    compat = new { supportsTools = true, supportsUsageInStreaming = true },
                },
            },
        };
        return JsonSerializer.Serialize(value);
    }

    public static string BuildPrimaryModel(LocalAiResolvedInstall install)
    {
        ArgumentNullException.ThrowIfNull(install);
        return $"llamacpp/{install.Manifest.ModelAlias}";
    }

    public static string BuildProviderBatchJson(LocalAiResolvedInstall install)
    {
        using JsonDocument provider = JsonDocument.Parse(BuildProviderJson(install));
        return JsonSerializer.Serialize(new[]
        {
            new { path = ProviderPath, value = (object)provider.RootElement.Clone() },
        });
    }
}
