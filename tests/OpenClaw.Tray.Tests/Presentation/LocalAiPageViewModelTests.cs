using OpenClaw.Connection;
using OpenClaw.Connection.LocalAi;
using OpenClawTray.Presentation;

namespace OpenClaw.Tray.Tests.Presentation;

public sealed class LocalAiPageViewModelTests
{
    private const string ModelTag = "qwen3.6:35b-a3b-mtp-q4_K_M";

    [Fact]
    public void ManagedHealthy_ShowsExactManifestFactsAndManagedControls()
    {
        var runtime = new ControllableLocalAiRuntime(Snapshot(
            LocalAiRuntimeState.Healthy,
            LocalAiOwnership.Managed,
            modelTag: ModelTag,
            version: "0.32.14",
            processId: 4242));
        var host = ConnectedGateway();
        using var source = new PermissionsPageRuntimeSource(host);
        var commands = new FakeAppCommands();
        using var vm = new LocalAiPageViewModel(runtime, source, commands, new RecordingUiDispatcher());

        vm.Activate(null);

        Assert.Equal(LocalAiEnginePresentationState.Running, vm.EngineState);
        Assert.Equal("LocalAiPage_Engine_Managed", vm.EngineOwnershipResourceKey);
        Assert.Equal("0.32.14", vm.EngineVersion);
        Assert.Equal(ModelTag, vm.ModelTag);
        Assert.Equal(LocalAiModelPresentationState.Unknown, vm.ModelState);
        Assert.Equal("256K", LocalAiPageViewModel.ContextLengthText);
        Assert.Equal("FP16", LocalAiPageViewModel.KvCacheText);
        Assert.False(vm.CanStart);
        Assert.True(vm.CanStop);
        Assert.True(vm.CanRestart);
        Assert.True(vm.CanOpenLogs);
        Assert.True(vm.CanRetrySetup);
        Assert.Equal(LocalAiGatewayPresentationState.Connected, vm.GatewayState);
        Assert.Equal("Test gateway", vm.GatewayDetail);
    }

    [Fact]
    public async Task ExternalHealthy_NeverInvokesManagedRuntimeControlsOrLogs()
    {
        var runtime = new ControllableLocalAiRuntime(Snapshot(
            LocalAiRuntimeState.Healthy,
            LocalAiOwnership.External,
            modelTag: ModelTag,
            version: "0.32.14",
            processId: 5150));
        using var source = new PermissionsPageRuntimeSource(ConnectedGateway());
        var commands = new FakeAppCommands();
        using var vm = new LocalAiPageViewModel(runtime, source, commands, new RecordingUiDispatcher());
        vm.Activate(null);

        Assert.True(vm.IsExternal);
        Assert.Equal("LocalAiPage_Engine_External", vm.EngineOwnershipResourceKey);
        Assert.False(vm.CanStart);
        Assert.False(vm.CanStop);
        Assert.False(vm.CanRestart);
        Assert.False(vm.CanOpenLogs);
        Assert.False(vm.CanRetrySetup);
        Assert.False(await vm.StartAsync());
        Assert.False(await vm.StopAsync());
        Assert.False(await vm.RestartAsync());
        Assert.False(vm.OpenLogs());
        Assert.Equal(0, runtime.EnsureStartedCount);
        Assert.Equal(0, runtime.StopCount);
        Assert.Equal(0, runtime.RestartCount);
        Assert.Equal(0, commands.OpenLocalAiLogsCount);
    }

    [Fact]
    public async Task StoppedManagedInstall_StartsOpensLogsAndOffersHonestDownloadRetry()
    {
        var stopped = Snapshot(LocalAiRuntimeState.Stopped, LocalAiOwnership.None, modelTag: ModelTag, version: "0.32.14");
        var runtime = new ControllableLocalAiRuntime(stopped)
        {
            EnsureStartedResult = Snapshot(LocalAiRuntimeState.Healthy, LocalAiOwnership.Managed, modelTag: ModelTag, version: "0.32.14"),
        };
        using var source = new PermissionsPageRuntimeSource(ConnectedGateway());
        var commands = new FakeAppCommands();
        using var vm = new LocalAiPageViewModel(runtime, source, commands, new RecordingUiDispatcher());
        vm.Activate(null);

        Assert.Equal("LocalAiPage_Engine_Managed", vm.EngineOwnershipResourceKey);
        Assert.True(vm.CanStart);
        Assert.True(vm.CanOpenLogs);
        Assert.True(vm.CanRetrySetup);
        Assert.True(await vm.StartAsync());
        Assert.Equal(1, runtime.EnsureStartedCount);
        Assert.Equal(LocalAiEnginePresentationState.Running, vm.EngineState);
        Assert.True(vm.OpenLogs());
        Assert.Equal(1, commands.OpenLocalAiLogsCount);
    }

    [Theory]
    [InlineData(LocalAiRuntimeState.NotInstalled, (int)LocalAiModelPresentationState.NotInstalled)]
    [InlineData(LocalAiRuntimeState.Stopped, (int)LocalAiModelPresentationState.Unknown)]
    [InlineData(LocalAiRuntimeState.Failed, (int)LocalAiModelPresentationState.Unknown)]
    public void MissingOrUnknownModel_RetrySetupUsesExistingOnboardingCommand(
        LocalAiRuntimeState runtimeState,
        int expectedModelState)
    {
        var runtime = new ControllableLocalAiRuntime(Snapshot(runtimeState, LocalAiOwnership.None));
        using var source = new PermissionsPageRuntimeSource(ConnectedGateway());
        var commands = new FakeAppCommands();
        using var vm = new LocalAiPageViewModel(runtime, source, commands, new RecordingUiDispatcher());
        vm.Activate(null);

        Assert.Equal((LocalAiModelPresentationState)expectedModelState, vm.ModelState);
        Assert.True(vm.CanRetrySetup);
        Assert.True(vm.RetrySetup());
        Assert.Equal(1, commands.ShowOnboardingCount);
    }

    [Fact]
    public void ManifestTag_DoesNotClaimDownloadedOrLoaded_AndOffersSetupRetry()
    {
        var runtime = new ControllableLocalAiRuntime(Snapshot(
            LocalAiRuntimeState.Failed,
            LocalAiOwnership.None,
            modelTag: ModelTag,
            detail: "Managed Ollama exited."));
        using var source = new PermissionsPageRuntimeSource(ConnectedGateway());
        using var vm = new LocalAiPageViewModel(runtime, source, new FakeAppCommands(), new RecordingUiDispatcher());
        vm.Activate(null);

        Assert.Equal(LocalAiEnginePresentationState.Error, vm.EngineState);
        Assert.Equal(LocalAiModelPresentationState.Unknown, vm.ModelState);
        Assert.True(vm.CanRetrySetup);
        Assert.True(vm.CanStart);
    }

    [Fact]
    public void RuntimeEvents_MarshalToUiAndDeactivateReleasesSubscriptions()
    {
        var runtime = new ControllableLocalAiRuntime(Snapshot(LocalAiRuntimeState.Stopped, LocalAiOwnership.None, modelTag: ModelTag));
        var dispatcher = new RecordingUiDispatcher { HasThreadAccess = false, RunEnqueuedImmediately = false };
        using var source = new PermissionsPageRuntimeSource(ConnectedGateway());
        using var vm = new LocalAiPageViewModel(runtime, source, new FakeAppCommands(), dispatcher);
        vm.Activate(null);
        Assert.Equal(1, runtime.SubscriberCount);

        runtime.SetSnapshot(Snapshot(LocalAiRuntimeState.Healthy, LocalAiOwnership.Managed, modelTag: ModelTag));
        Assert.Equal(LocalAiEnginePresentationState.Stopped, vm.EngineState);
        Assert.Equal(1, dispatcher.EnqueuedCount);

        dispatcher.FlushPending();
        Assert.Equal(LocalAiEnginePresentationState.Running, vm.EngineState);

        vm.Deactivate();
        Assert.Equal(0, runtime.SubscriberCount);
        runtime.SetSnapshot(Snapshot(LocalAiRuntimeState.Failed, LocalAiOwnership.None, modelTag: ModelTag));
        Assert.Equal(1, dispatcher.EnqueuedCount);
        Assert.Equal(LocalAiEnginePresentationState.Running, vm.EngineState);
    }

    [Fact]
    public void GatewayStateAndActionsFollowAuthoritativeRuntimeSource()
    {
        var runtime = new ControllableLocalAiRuntime(Snapshot(LocalAiRuntimeState.Stopped, LocalAiOwnership.None, modelTag: ModelTag));
        var host = new FakePermissionsPageRuntimeHost
        {
            ConnectionSnapshot = new GatewayConnectionSnapshot
            {
                OverallState = OverallConnectionState.Idle,
                OperatorState = RoleConnectionState.Idle,
                NodeState = RoleConnectionState.Disabled,
            },
        };
        using var source = new PermissionsPageRuntimeSource(host);
        var commands = new FakeAppCommands();
        using var vm = new LocalAiPageViewModel(runtime, source, commands, new RecordingUiDispatcher());
        vm.Activate(null);

        Assert.Equal(LocalAiGatewayPresentationState.Disconnected, vm.GatewayState);
        Assert.True(vm.CanRepairConnection);
        Assert.False(vm.CanOpenChat);
        Assert.True(vm.RepairConnection());
        Assert.Equal(1, commands.ReconnectCount);

        host.ConnectionSnapshot = ConnectedGateway().ConnectionSnapshot;
        host.RaiseChanged();

        Assert.Equal(LocalAiGatewayPresentationState.Connected, vm.GatewayState);
        Assert.False(vm.CanRepairConnection);
        Assert.True(vm.CanOpenChat);
        Assert.True(vm.OpenChat());
        Assert.Equal(1, commands.ShowChatCount);
    }

    [Fact]
    public async Task RuntimeActionFailure_IsContainedAndPresented()
    {
        var runtime = new ControllableLocalAiRuntime(Snapshot(LocalAiRuntimeState.Stopped, LocalAiOwnership.None, modelTag: ModelTag))
        {
            EnsureStartedException = new InvalidOperationException("start failed"),
        };
        using var source = new PermissionsPageRuntimeSource(ConnectedGateway());
        using var vm = new LocalAiPageViewModel(runtime, source, new FakeAppCommands(), new RecordingUiDispatcher());
        vm.Activate(null);

        Assert.False(await vm.StartAsync());
        Assert.Equal("start failed", vm.ActionError);
        Assert.False(vm.IsBusy);
        Assert.True(vm.CanStart);
    }

    private static FakePermissionsPageRuntimeHost ConnectedGateway() => new()
    {
        ConnectionSnapshot = new GatewayConnectionSnapshot
        {
            OverallState = OverallConnectionState.Ready,
            OperatorState = RoleConnectionState.Connected,
            NodeState = RoleConnectionState.Connected,
            GatewayName = "Test gateway",
            GatewayUrl = "ws://127.0.0.1:18789",
        },
    };

    private static LocalAiRuntimeSnapshot Snapshot(
        LocalAiRuntimeState state,
        LocalAiOwnership ownership,
        string? modelTag = null,
        string? version = null,
        int? processId = null,
        string? detail = null) =>
        new(
            state,
            ownership,
            new Uri("http://127.0.0.1:11434"),
            version,
            modelTag,
            processId,
            processId.HasValue ? DateTimeOffset.Parse("2026-08-17T12:00:00Z") : null,
            detail,
            DateTimeOffset.Parse("2026-08-17T12:00:01Z"));

    private sealed class ControllableLocalAiRuntime : ILocalAiRuntime
    {
        private EventHandler<LocalAiRuntimeSnapshotChangedEventArgs>? _stateChanged;

        public ControllableLocalAiRuntime(LocalAiRuntimeSnapshot snapshot)
        {
            Snapshot = snapshot;
            EnsureStartedResult = snapshot;
            StopResult = snapshot;
            RestartResult = snapshot;
        }

        public LocalAiRuntimeSnapshot Snapshot { get; private set; }
        public LocalAiRuntimeSnapshot EnsureStartedResult { get; set; }
        public LocalAiRuntimeSnapshot StopResult { get; set; }
        public LocalAiRuntimeSnapshot RestartResult { get; set; }
        public Exception? EnsureStartedException { get; set; }
        public int EnsureStartedCount { get; private set; }
        public int StopCount { get; private set; }
        public int RestartCount { get; private set; }
        public int SubscriberCount { get; private set; }

        public event EventHandler<LocalAiRuntimeSnapshotChangedEventArgs>? StateChanged
        {
            add { _stateChanged += value; SubscriberCount++; }
            remove { _stateChanged -= value; SubscriberCount--; }
        }

        public Task<LocalAiRuntimeSnapshot> EnsureStartedAsync(CancellationToken cancellationToken = default)
        {
            EnsureStartedCount++;
            if (EnsureStartedException is not null)
                return Task.FromException<LocalAiRuntimeSnapshot>(EnsureStartedException);
            SetSnapshot(EnsureStartedResult);
            return Task.FromResult(Snapshot);
        }

        public Task<LocalAiRuntimeSnapshot> StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            SetSnapshot(StopResult);
            return Task.FromResult(Snapshot);
        }

        public Task<LocalAiRuntimeSnapshot> RestartAsync(CancellationToken cancellationToken = default)
        {
            RestartCount++;
            SetSnapshot(RestartResult);
            return Task.FromResult(Snapshot);
        }

        public Task<LocalAiRuntimeSnapshot> RefreshAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Snapshot);

        public void SetSnapshot(LocalAiRuntimeSnapshot snapshot)
        {
            Snapshot = snapshot;
            _stateChanged?.Invoke(this, new LocalAiRuntimeSnapshotChangedEventArgs(snapshot));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
