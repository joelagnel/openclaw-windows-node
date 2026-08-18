using System.Text;
using System.Text.Json;
using OpenClaw.TestSupport;

namespace OpenClaw.SetupEngine.Tests;

public sealed class LocalAiGatewayConfigurationTests : IDisposable
{
    private readonly TempDirectory _temp = new("openclaw-local-ai-gateway-");

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void BuildBatchJson_ContainsQualifiedProviderAndPrimaryModel()
    {
        var config = EnabledConfig();

        using var document = JsonDocument.Parse(LocalAiGatewayConfigBuilder.BuildBatchJson(config));

        var operations = document.RootElement.EnumerateArray().ToArray();
        Assert.Equal(2, operations.Length);
        Assert.Equal("models.providers.ollama", operations[0].GetProperty("path").GetString());
        var provider = operations[0].GetProperty("value");
        Assert.Equal("http://127.0.0.1:11434", provider.GetProperty("baseUrl").GetString());
        Assert.Equal("ollama", provider.GetProperty("api").GetString());
        Assert.Equal(300, provider.GetProperty("timeoutSeconds").GetInt32());
        var model = Assert.Single(provider.GetProperty("models").EnumerateArray().ToArray());
        Assert.Equal(LocalAiConfig.DefaultModel, model.GetProperty("id").GetString());
        Assert.Equal(262_144, model.GetProperty("contextWindow").GetInt32());
        Assert.Equal(262_144, model.GetProperty("contextTokens").GetInt32());
        Assert.Equal(262_144, model.GetProperty("params").GetProperty("num_ctx").GetInt32());
        Assert.True(model.GetProperty("compat").GetProperty("supportsTools").GetBoolean());
        Assert.Equal("agents.defaults.model.primary", operations[1].GetProperty("path").GetString());
        Assert.Equal($"ollama/{LocalAiConfig.DefaultModel}", operations[1].GetProperty("value").GetString());
    }

    [Theory]
    [InlineData("http://0.0.0.0:11434")]
    [InlineData("http://127.0.0.1:9999")]
    [InlineData("https://127.0.0.1:11434")]
    [InlineData("http://example.test:11434")]
    public void Validate_RejectsNonQualifiedEndpoint(string endpoint)
    {
        var config = EnabledConfig();
        config.Endpoint = endpoint;

        Assert.NotNull(LocalAiSetupPolicy.Validate(config));
    }

    [Fact]
    public async Task Execute_UsesOneValidatedBatchFileThroughStdinScript()
    {
        var commands = new RecordingCommandRunner
        {
            WslResult = new(0, "Dry run successful\nLOCAL_AI_GATEWAY_CONFIGURED\n", "", TimeSpan.Zero, false),
        };
        var config = new SetupConfig { LocalAi = EnabledConfig() };
        var ctx = CreateContext(config, commands);

        var result = await new ConfigureLocalAiGatewayStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.IsSuccess);
        var call = Assert.Single(commands.WslCalls);
        Assert.True(call.InputViaStdin);
        Assert.Equal(config.Wsl.User, call.User);
        Assert.Contains("--dry-run", call.Command, StringComparison.Ordinal);
        Assert.Contains("openclaw config set --batch-file", call.Command, StringComparison.Ordinal);
        var encoded = Assert.Contains("OPENCLAW_LOCAL_AI_BATCH_B64", call.Environment!);
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        Assert.Equal(LocalAiGatewayConfigBuilder.BuildBatchJson(config.LocalAi), decoded);
    }

    [Fact]
    public void CanSkip_WhenLocalAiDisabled()
    {
        var ctx = CreateContext(new SetupConfig(), new RecordingCommandRunner());

        Assert.True(new ConfigureLocalAiGatewayStep().CanSkip(ctx));
    }

    [Fact]
    public void GatewayWizard_SkipsWhenLocalAiOwnsProviderSelection()
    {
        var ctx = CreateContext(
            new SetupConfig { LocalAi = EnabledConfig(), SkipWizard = false },
            new RecordingCommandRunner());

        Assert.True(new RunGatewayWizardStep().CanSkip(ctx));
    }

    private SetupContext CreateContext(SetupConfig config, ICommandRunner commands) =>
        new(
            config,
            new SetupLogger(null, LogLevel.Trace),
            new TransactionJournal(null),
            commands,
            CancellationToken.None,
            dataDir: _temp.Combine("data"),
            localDataDir: _temp.Combine("local"));

    private static LocalAiConfig EnabledConfig() => new() { Enabled = true };

    private sealed class RecordingCommandRunner : ICommandRunner
    {
        public CommandResult WslResult { get; init; } = new(0, "", "", TimeSpan.Zero, false);
        public List<WslCall> WslCalls { get; } = [];

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
            WslCalls.Add(new(command, environment, user, inputViaStdin));
            return Task.FromResult(WslResult);
        }
    }

    private sealed record WslCall(
        string Command,
        IReadOnlyDictionary<string, string>? Environment,
        string? User,
        bool InputViaStdin);
}
