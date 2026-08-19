using OpenClaw.Connection;
using OpenClaw.Connection.LocalAi;
using OpenClaw.Shared;
using OpenClaw.Shared.Inference.Catalog;
using OpenClawTray.Services;
using System.Collections.Immutable;

namespace OpenClaw.Tray.Tests;

public sealed class LocalAiGatewayProviderCoordinatorTests
{
    [Fact]
    public async Task Quiesce_RemovesOnlyExactManagedProvider()
    {
        LocalAiResolvedInstall install = Install(28_765);
        string expected = LocalAiGatewayProviderDefinition.BuildProviderJson(install);
        var commands = new FakeWslCommandRunner(expected);
        var coordinator = new LocalAiGatewayProviderCoordinator(commands, "OpenClawGateway", NullLogger.Instance);

        LocalAiEndpointLifecycleResult result = await coordinator.QuiesceAsync(install);

        Assert.True(result.Success);
        Assert.Null(commands.ProviderJson);
        Assert.Contains(commands.Calls, call => call.Contains("unset"));
    }

    [Fact]
    public async Task Quiesce_AcceptsCliRedactedManagedApiKey()
    {
        LocalAiResolvedInstall install = Install(28_765);
        string observed = RedactApiKey(LocalAiGatewayProviderDefinition.BuildProviderJson(install));
        var commands = new FakeWslCommandRunner(observed);
        var coordinator = new LocalAiGatewayProviderCoordinator(commands, "OpenClawGateway", NullLogger.Instance);

        LocalAiEndpointLifecycleResult result = await coordinator.QuiesceAsync(install);

        Assert.True(result.Success);
        Assert.Null(commands.ProviderJson);
    }

    [Fact]
    public async Task Quiesce_PreservesProviderDriftAndFailsClosed()
    {
        LocalAiResolvedInstall install = Install(28_765);
        string drifted = LocalAiGatewayProviderDefinition.BuildProviderJson(install)
            .Replace("28765", "39876", StringComparison.Ordinal);
        var commands = new FakeWslCommandRunner(drifted);
        var coordinator = new LocalAiGatewayProviderCoordinator(commands, "OpenClawGateway", NullLogger.Instance);

        LocalAiEndpointLifecycleResult result = await coordinator.QuiesceAsync(install);

        Assert.False(result.Success);
        Assert.Equal(drifted, commands.ProviderJson);
        Assert.DoesNotContain(commands.Calls, call => call.Contains("unset"));
    }

    [Fact]
    public async Task Publish_UsesVerifiedEndpointAndNoShellVariableExpansion()
    {
        LocalAiResolvedInstall install = Install(28_766);
        string expected = LocalAiGatewayProviderDefinition.BuildProviderJson(install);
        var commands = new FakeWslCommandRunner(providerJson: null)
        {
            ProviderAfterApply = RedactApiKey(expected),
        };
        var coordinator = new LocalAiGatewayProviderCoordinator(commands, "OpenClawGateway", NullLogger.Instance);

        LocalAiEndpointLifecycleResult result = await coordinator.PublishAsync(install);

        Assert.True(result.Success);
        Assert.True(LocalAiGatewayProviderDefinition.MatchesProviderJson(commands.ProviderJson!, install));
        IReadOnlyList<string> apply = Assert.Single(commands.Calls, call => call.Contains("/bin/sh"));
        string script = apply[^1];
        Assert.Contains("--dry-run", script, StringComparison.Ordinal);
        Assert.DoesNotContain('$', script);
        Assert.All(commands.Distros, distro => Assert.Equal("OpenClawGateway", distro));
    }

    [Fact]
    public async Task Publish_VerificationFailureRemovesExactJustWrittenProvider()
    {
        LocalAiResolvedInstall install = Install(28_768);
        string expected = LocalAiGatewayProviderDefinition.BuildProviderJson(install);
        var commands = new FakeWslCommandRunner(providerJson: null)
        {
            ProviderAfterApply = expected,
            FailedReadCalls = [2],
        };
        var coordinator = new LocalAiGatewayProviderCoordinator(commands, "OpenClawGateway", NullLogger.Instance);

        LocalAiEndpointLifecycleResult result = await coordinator.PublishAsync(install);

        Assert.False(result.Success);
        Assert.Contains("was removed", result.Detail, StringComparison.Ordinal);
        Assert.Null(commands.ProviderJson);
        Assert.Contains(commands.Calls, call => call.Contains("unset"));
    }

    [Fact]
    public async Task Publish_VerificationFailureSurfacesCleanupFailure()
    {
        LocalAiResolvedInstall install = Install(28_769);
        string expected = LocalAiGatewayProviderDefinition.BuildProviderJson(install);
        var commands = new FakeWslCommandRunner(providerJson: null)
        {
            ProviderAfterApply = expected,
            FailedReadCalls = [2, 3],
        };
        var coordinator = new LocalAiGatewayProviderCoordinator(commands, "OpenClawGateway", NullLogger.Instance);

        LocalAiEndpointLifecycleResult result = await coordinator.PublishAsync(install);

        Assert.False(result.Success);
        Assert.Contains("Cleanup also failed", result.Detail, StringComparison.Ordinal);
        Assert.Equal(expected, commands.ProviderJson);
        Assert.DoesNotContain(commands.Calls, call => call.Contains("unset"));
    }

    [Fact]
    public async Task Quiesce_DoesNotMistakeWslFailureForMissingProvider()
    {
        LocalAiResolvedInstall install = Install(28_767);
        var commands = new FakeWslCommandRunner(providerJson: null) { FailReads = true };
        var coordinator = new LocalAiGatewayProviderCoordinator(commands, "OpenClawGateway", NullLogger.Instance);

        LocalAiEndpointLifecycleResult result = await coordinator.QuiesceAsync(install);

        Assert.False(result.Success);
    }

    private static LocalAiResolvedInstall Install(int port)
    {
        var endpoint = new Uri($"http://127.0.0.1:{port}/v1");
        var manifest = new LocalAiInstallManifest
        {
            EngineVersion = "b10488",
            Architecture = "arm64",
            HardwareProfileId = "rtx-spark-n1x",
            RuntimeId = "b10488-cuda13-arm64",
            ModelCatalogId = LocalModelCatalog.Qwen35BModelId,
            SelectedGpuId = "GPU-SPARK",
            ExecutablePath = "engines/llama-server.exe",
            RuntimeAssets = ImmutableArray<LocalAiAssetReceipt>.Empty,
            ModelPath = "models/model.gguf",
            ModelId = "owner/model@0123456789abcdef0123456789abcdef01234567",
            ModelAlias = LocalModelCatalog.Qwen35BModelId,
            ModelAsset = new LocalAiAssetReceipt
            {
                FileName = "model.gguf",
                SourceUrl = "https://huggingface.co/owner/model/resolve/0123456789abcdef0123456789abcdef01234567/model.gguf",
                SizeBytes = 1,
                Sha256 = new string('a', 64),
            },
            RequestedPort = 0,
            Endpoint = endpoint.AbsoluteUri,
            ContextLength = LocalModelCatalog.NativeContextTokens,
        };
        return new(manifest, "llama-server.exe", "model.gguf", endpoint);
    }

    private static string RedactApiKey(string value) => value.Replace(
        "llama-local",
        LocalAiGatewayProviderDefinition.CliRedactedApiKey,
        StringComparison.Ordinal);

    private sealed class FakeWslCommandRunner(string? providerJson) : IWslCommandRunner
    {
        public string? ProviderJson { get; private set; } = providerJson;
        public string? ProviderAfterApply { get; init; }
        public bool FailReads { get; init; }
        public HashSet<int> FailedReadCalls { get; init; } = [];
        public List<IReadOnlyList<string>> Calls { get; } = [];
        public List<string> Distros { get; } = [];
        private int _readCalls;

        public Task<WslCommandResult> RunInDistroAsync(
            string name,
            IReadOnlyList<string> command,
            CancellationToken cancellationToken = default,
            IReadOnlyDictionary<string, string>? environment = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Distros.Add(name);
            Calls.Add(command.ToArray());
            bool providerRead = command.Contains("get") &&
                command.Contains(LocalAiGatewayProviderDefinition.ProviderPath);
            if (providerRead && (FailReads || FailedReadCalls.Contains(++_readCalls)))
                return Result(1, string.Empty, "wsl.exe failed");
            if (command.Contains("/bin/sh"))
            {
                ProviderJson = ProviderAfterApply;
                return Result(ProviderJson is null ? 1 : 0, string.Empty);
            }
            if (command.Contains("unset"))
            {
                ProviderJson = null;
                return Result(0, string.Empty);
            }
            if (command.Contains(LocalAiGatewayProviderDefinition.ProviderPath))
                return ProviderJson is null
                    ? Result(
                        1,
                        string.Empty,
                        $"Config path not found: {LocalAiGatewayProviderDefinition.ProviderPath}")
                    : Result(0, ProviderJson);
            return Result(1, string.Empty);
        }

        public Task<WslCommandResult> RunAsync(
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default,
            IReadOnlyDictionary<string, string>? environment = null) => Result(1, string.Empty);

        public Task<IReadOnlyList<WslDistroInfo>> ListDistrosAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WslDistroInfo>>([]);

        public Task<WslCommandResult> TerminateDistroAsync(
            string name,
            CancellationToken cancellationToken = default) => Result(1, string.Empty);

        public Task<WslCommandResult> UnregisterDistroAsync(
            string name,
            CancellationToken cancellationToken = default) => Result(1, string.Empty);

        private static Task<WslCommandResult> Result(
            int exitCode,
            string stdout,
            string stderr = "") =>
            Task.FromResult(new WslCommandResult(exitCode, stdout, stderr));
    }
}
