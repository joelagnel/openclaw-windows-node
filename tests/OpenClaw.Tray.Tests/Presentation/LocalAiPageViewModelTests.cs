using OpenClaw.Connection;
using OpenClaw.Connection.LocalAi;
using OpenClawTray.Presentation;

namespace OpenClaw.Tray.Tests.Presentation;

public sealed class LocalAiPageViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 18, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void HealthyRouterWithVerifiedModel_EnablesManagedControlsAndChat()
    {
        var runtime = new FakeRuntime(Snapshot(
            LocalAiRuntimeState.Healthy,
            LocalAiOwnership.CompanionManaged,
            LocalAiModelAvailabilityState.Verified,
            processId: 4120));
        using var source = ConnectedSource();
        var commands = new FakeAppCommands();
        using var viewModel = new LocalAiPageViewModel(runtime, source, commands, new RecordingUiDispatcher());

        viewModel.Activate(null);

        Assert.Equal(LocalAiEnginePresentationState.Running, viewModel.EngineState);
        Assert.Equal(LocalAiModelPresentationState.Verified, viewModel.ModelState);
        Assert.Equal("Qwen3.6 35B-A3B (UD-Q4_K_M)", viewModel.ModelName);
        Assert.Equal("4120", viewModel.ProcessId);
        Assert.True(viewModel.CanRestart);
        Assert.True(viewModel.CanOpenLogs);
        Assert.True(viewModel.CanOpenChat);
        Assert.True(viewModel.OpenLogs());
        Assert.True(viewModel.OpenChat());
        Assert.Equal(1, commands.OpenLocalAiLogsCount);
        Assert.Equal(1, commands.ShowChatCount);
    }

    [Fact]
    public void StoppedVerifiedRouter_CanStartWithoutClaimingModelLoaded()
    {
        var runtime = new FakeRuntime(Snapshot(
            LocalAiRuntimeState.Stopped,
            LocalAiOwnership.None,
            LocalAiModelAvailabilityState.Verified));
        using var source = ConnectedSource();
        using var viewModel = new LocalAiPageViewModel(runtime, source, new FakeAppCommands(), new RecordingUiDispatcher());

        Assert.Equal(LocalAiEnginePresentationState.Stopped, viewModel.EngineState);
        Assert.Equal(LocalAiModelPresentationState.Verified, viewModel.ModelState);
        Assert.True(viewModel.CanStart);
        Assert.False(viewModel.CanOpenChat);
        Assert.Equal("Not running", viewModel.ProcessId ?? "Not running");
    }

    [Fact]
    public void MissingInstall_OffersSetupRetryOnly()
    {
        var runtime = new FakeRuntime(Snapshot(
            LocalAiRuntimeState.NotInstalled,
            LocalAiOwnership.None,
            LocalAiModelAvailabilityState.NotInstalled,
            modelId: null));
        using var source = ConnectedSource();
        var commands = new FakeAppCommands();
        using var viewModel = new LocalAiPageViewModel(runtime, source, commands, new RecordingUiDispatcher());

        Assert.True(viewModel.CanRetrySetup);
        Assert.False(viewModel.CanStart);
        Assert.False(viewModel.CanOpenLogs);
        Assert.True(viewModel.RetrySetup());
        Assert.Equal(1, commands.ShowOnboardingCount);
    }

    [Fact]
    public void ActivateRefreshesAndDisposeUnsubscribes()
    {
        var runtime = new FakeRuntime(Snapshot(
            LocalAiRuntimeState.Stopped,
            LocalAiOwnership.None,
            LocalAiModelAvailabilityState.Verified));
        using var source = ConnectedSource();
        var viewModel = new LocalAiPageViewModel(runtime, source, new FakeAppCommands(), new RecordingUiDispatcher());

        viewModel.Activate(null);
        Assert.Equal(1, runtime.RefreshCount);
        viewModel.Dispose();

        Assert.True(viewModel.IsDisposed);
        Assert.False(viewModel.IsActive);
    }

    private static PermissionsPageRuntimeSource ConnectedSource()
    {
        var host = new FakePermissionsPageRuntimeHost
        {
            ConnectionSnapshot = new GatewayConnectionSnapshot
            {
                OverallState = OverallConnectionState.Ready,
                OperatorState = RoleConnectionState.Connected,
                NodeState = RoleConnectionState.Connected,
                GatewayName = "OpenClawGateway",
                GatewayUrl = "ws://127.0.0.1:18789",
            },
        };
        return new PermissionsPageRuntimeSource(host);
    }

    private static LocalAiRuntimeSnapshot Snapshot(
        LocalAiRuntimeState state,
        LocalAiOwnership ownership,
        LocalAiModelAvailabilityState modelState,
        int? processId = null,
        string? modelId = "qwen3.6-35b-a3b-mtp-q4-k-m") =>
        new(
            state,
            ownership,
            new Uri("http://127.0.0.1:18808"),
            "b10488",
            modelId,
            modelState switch
            {
                LocalAiModelAvailabilityState.Verified => new LocalAiModelEvidence(
                    modelState, Now, new string('a', 64), 22_663_387_424),
                LocalAiModelAvailabilityState.Loaded => new LocalAiModelEvidence(
                    modelState, Now, new string('a', 64), 22_663_387_424, "qwen3.6-35b-a3b-q4-k-m"),
                LocalAiModelAvailabilityState.NotInstalled => LocalAiModelEvidence.NotInstalled(Now),
                _ => LocalAiModelEvidence.Unknown(Now),
            },
            processId,
            processId.HasValue ? Now : null,
            null,
            Now);

    private sealed class FakeRuntime(LocalAiRuntimeSnapshot snapshot) : ILocalAiRuntime
    {
        public int RefreshCount { get; private set; }
        public LocalAiRuntimeSnapshot Snapshot { get; private set; } = snapshot;
        public event EventHandler<LocalAiRuntimeSnapshotChangedEventArgs>? StateChanged
        {
            add { }
            remove { }
        }
        public Task<LocalAiRuntimeSnapshot> EnsureStartedAsync(CancellationToken cancellationToken = default) => Task.FromResult(Snapshot);
        public Task<LocalAiRuntimeSnapshot> StopAsync(CancellationToken cancellationToken = default) => Task.FromResult(Snapshot);
        public Task<LocalAiRuntimeSnapshot> RestartAsync(CancellationToken cancellationToken = default) => Task.FromResult(Snapshot);
        public Task<LocalAiRuntimeSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
        {
            RefreshCount++;
            return Task.FromResult(Snapshot);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
