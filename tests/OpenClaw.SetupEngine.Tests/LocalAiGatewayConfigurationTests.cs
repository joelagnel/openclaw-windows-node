using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using OpenClaw.Connection.LocalAi;
using OpenClaw.Shared.Inference;
using OpenClaw.Shared.Inference.Catalog;
using OpenClaw.TestSupport;

namespace OpenClaw.SetupEngine.Tests;

public sealed class LocalAiGatewayConfigurationTests
{
    [Fact]
    public void BuildBatchJson_UsesOpenAiCompatibleProviderAndExactDynamicEndpoint()
    {
        using var temp = new TempDirectory("local-ai-gateway-");
        SetupContext context = CreateContext(temp.Path, new QueueCommandRunner());

        using JsonDocument document = JsonDocument.Parse(LocalAiGatewayConfigBuilder.BuildBatchJson(context));

        JsonElement[] operations = document.RootElement.EnumerateArray().ToArray();
        Assert.Equal(2, operations.Length);
        Assert.Equal("models.providers.llamacpp", operations[0].GetProperty("path").GetString());
        JsonElement provider = operations[0].GetProperty("value");
        Assert.Equal("http://127.0.0.1:49151/v1", provider.GetProperty("baseUrl").GetString());
        Assert.Equal("openai-completions", provider.GetProperty("api").GetString());
        Assert.Equal("llama-local", provider.GetProperty("apiKey").GetString());
        JsonElement model = Assert.Single(provider.GetProperty("models").EnumerateArray().ToArray());
        Assert.Equal(LocalModelCatalog.Qwen35BModelId, model.GetProperty("id").GetString());
        Assert.Equal(LocalModelCatalog.NativeContextTokens, model.GetProperty("contextWindow").GetInt32());
        Assert.Equal(["text"], model.GetProperty("input").EnumerateArray().Select(value => value.GetString()));
        Assert.False(model.TryGetProperty("params", out _));
        Assert.Equal("agents.defaults.model.primary", operations[1].GetProperty("path").GetString());
        Assert.Equal($"llamacpp/{LocalModelCatalog.Qwen35BModelId}", operations[1].GetProperty("value").GetString());
    }

    [Fact]
    public async Task Execute_SnapshotsThenAppliesOneValidatedBatchAndSkipsWizard()
    {
        using var temp = new TempDirectory("local-ai-gateway-");
        var commands = new QueueCommandRunner(
            Snapshot(providerJson: null, primaryJson: null),
            new CommandResult(0, "LOCAL_AI_GATEWAY_CONFIGURED\n", "", TimeSpan.Zero, false));
        SetupContext context = CreateContext(temp.Path, commands);

        StepResult result = await new ConfigureLocalAiGatewayStep().ExecuteAsync(context, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, commands.Calls.Count);
        CommandCall apply = commands.Calls[1];
        Assert.True(apply.InputViaStdin);
        Assert.Contains("--dry-run", apply.Script, StringComparison.Ordinal);
        Assert.Equal(context.Config.Wsl.User, apply.User);
        string encoded = Assert.Contains("OPENCLAW_LOCAL_AI_BATCH_B64", apply.Environment!);
        Assert.Equal(
            LocalAiGatewayConfigBuilder.BuildBatchJson(context),
            Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
    }

    [Fact]
    public async Task Rollback_RemovesOnlyUnchangedSetupCreatedValues()
    {
        using var temp = new TempDirectory("local-ai-gateway-");
        var commands = new QueueCommandRunner(
            Snapshot(null, null),
            Success("LOCAL_AI_GATEWAY_CONFIGURED"));
        SetupContext context = CreateContext(temp.Path, commands);
        var step = new ConfigureLocalAiGatewayStep();
        Assert.True((await step.ExecuteAsync(context, CancellationToken.None)).IsSuccess);

        string batch = LocalAiGatewayConfigBuilder.BuildBatchJson(context);
        using JsonDocument batchDocument = JsonDocument.Parse(batch);
        string provider = batchDocument.RootElement[0].GetProperty("value").GetRawText();
        string primary = batchDocument.RootElement[1].GetProperty("value").GetRawText();
        commands.Enqueue(Snapshot(provider, primary));
        commands.Enqueue(Success("LOCAL_AI_GATEWAY_UNSET"));

        await step.RollbackAsync(context, CancellationToken.None);

        Assert.Equal(4, commands.Calls.Count);
        string unset = commands.Calls[3].Script;
        Assert.Contains("openclaw config unset agents.defaults.model.primary", unset, StringComparison.Ordinal);
        Assert.Contains("openclaw config unset models.providers.llamacpp", unset, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rollback_PreservesValuesChangedAfterSetup()
    {
        using var temp = new TempDirectory("local-ai-gateway-");
        var commands = new QueueCommandRunner(
            Snapshot(null, null),
            Success("LOCAL_AI_GATEWAY_CONFIGURED"));
        SetupContext context = CreateContext(temp.Path, commands);
        var step = new ConfigureLocalAiGatewayStep();
        Assert.True((await step.ExecuteAsync(context, CancellationToken.None)).IsSuccess);
        commands.Enqueue(Snapshot("{\"baseUrl\":\"http://user-edit.test/v1\"}", "\"cloud/model\""));

        await step.RollbackAsync(context, CancellationToken.None);

        Assert.Equal(3, commands.Calls.Count);
    }

    [Fact]
    public void GatewayWizard_SkipsWhenLocalAiOwnsProviderSelection()
    {
        using var temp = new TempDirectory("local-ai-gateway-");
        SetupContext context = CreateContext(temp.Path, new QueueCommandRunner());
        context.Config.SkipWizard = false;

        Assert.True(new RunGatewayWizardStep().CanSkip(context));
    }

    private static SetupContext CreateContext(string localDataDirectory, ICommandRunner commands)
    {
        var config = new SetupConfig { LocalAi = new LocalAiConfig { Enabled = true } };
        var logger = new SetupLogger(null, LogLevel.Trace);
        var context = new SetupContext(
            config,
            logger,
            new TransactionJournal(null),
            commands,
            CancellationToken.None,
            localDataDir: localDataDirectory)
        {
            LocalAiEligibility = LocalInferenceEligibility.Evaluate(SparkHardware()),
        };
        var model = LocalModelCatalog.Default;
        var manifest = new LocalAiInstallManifest
        {
            EngineVersion = LlamaRuntimeCatalog.ReleaseTag,
            Architecture = "arm64",
            HardwareProfileId = SupportedHardwareProfiles.RtxSparkN1XProfileId,
            RuntimeId = LlamaRuntimeCatalog.Arm64RuntimeId,
            ModelCatalogId = model.Id,
            SelectedGpuId = "GPU-SPARK",
            ExecutablePath = Path.Combine("engines", "llama-server.exe"),
            RuntimeAssets = [],
            ModelPath = Path.Combine("models", model.Weights.RelativePath),
            ModelId = "unsloth/Qwen3.6-35B-A3B-MTP-GGUF@5bc3e238d916f48a861bac2f8a1990a0e9b7e98d",
            ModelAlias = model.Id,
            ModelAsset = new LocalAiAssetReceipt
            {
                FileName = model.Weights.RelativePath,
                SourceUrl = model.Weights.DownloadUri.AbsoluteUri,
                SizeBytes = model.Weights.SizeBytes,
                Sha256 = model.Weights.Sha256.Value,
            },
            Endpoint = "http://127.0.0.1:49151/v1",
            ContextLength = model.Recipe.ContextTokens,
        };
        context.LocalAiResolvedInstall = new(
            manifest,
            Path.Combine(localDataDirectory, "LocalAI", manifest.ExecutablePath),
            Path.Combine(localDataDirectory, "LocalAI", manifest.ModelPath),
            new Uri(manifest.Endpoint));
        return context;
    }

    private static HostHardwareInfo SparkHardware() => new(
        Architecture.Arm64,
        128L * 1024 * 1024 * 1024,
        100L * 1024 * 1024 * 1024,
        [new GpuInfo(
            GpuVendor.Nvidia,
            "NVIDIA RTX Spark N1X",
            25_702_694_912,
            25_000_000_000,
            "616.00",
            13,
            "GPU-SPARK")],
        VulkanAvailable: false);

    private static CommandResult Snapshot(string? providerJson, string? primaryJson)
    {
        static string Encode(string? json) => json is null
            ? "MISSING"
            : Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        string stdout = $"OPENCLAW_LOCAL_AI_PROVIDER_B64={Encode(providerJson)}\n" +
            $"OPENCLAW_LOCAL_AI_PRIMARY_B64={Encode(primaryJson)}\n";
        return new(0, stdout, "", TimeSpan.Zero, false);
    }

    private static CommandResult Success(string marker) =>
        new(0, marker + "\n", "", TimeSpan.Zero, false);

    private sealed class QueueCommandRunner(params CommandResult[] results) : ICommandRunner
    {
        private readonly Queue<CommandResult> _results = new(results);
        public List<CommandCall> Calls { get; } = [];
        public void Enqueue(CommandResult result) => _results.Enqueue(result);

        public Task<CommandResult> RunAsync(
            string executable,
            string[] arguments,
            TimeSpan timeout,
            IReadOnlyDictionary<string, string>? environment = null,
            string? workingDirectory = null,
            string? stdinInput = null,
            CancellationToken ct = default,
            Stream? stdinStream = null) => throw new NotSupportedException();

        public Task<CommandResult> RunInWslAsync(
            string distroName,
            string command,
            TimeSpan timeout,
            IReadOnlyDictionary<string, string>? environment = null,
            CancellationToken ct = default,
            string? user = null,
            bool inputViaStdin = false)
        {
            Calls.Add(new(command, environment, user, inputViaStdin));
            return Task.FromResult(_results.Dequeue());
        }
    }

    private sealed record CommandCall(
        string Script,
        IReadOnlyDictionary<string, string>? Environment,
        string? User,
        bool InputViaStdin);
}
