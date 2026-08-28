using OpenClaw.Connection;
using OpenClaw.Connection.LocalAi;
using OpenClaw.Shared.Inference;
using OpenClaw.Shared.Inference.Catalog;
using OpenClawTray.Presentation;
using System.Runtime.InteropServices;

namespace OpenClaw.Tray.Tests.Presentation;

public sealed class LocalAiPageViewModelTests
{
    [Theory]
    [InlineData(LocalAiModelAvailabilityState.Verified)]
    [InlineData(LocalAiModelAvailabilityState.Loaded)]
    public void InstalledModel_OffersChangeModelThroughExistingSetupRoute(
        LocalAiModelAvailabilityState modelState)
    {
        using var harness = new LocalAiHarness(modelState);

        Assert.True(harness.ViewModel.CanChangeModel);
        Assert.True(harness.ViewModel.HasInstalledModel);
        Assert.False(harness.ViewModel.CanRetrySetup);

        Assert.True(harness.ViewModel.ChangeModel());

        Assert.Equal(0, harness.Commands.ShowGatewayWizardCount);
        Assert.Equal(1, harness.Commands.ShowOnboardingCount);
    }

    [Theory]
    [InlineData(LocalAiModelAvailabilityState.Unknown)]
    [InlineData(LocalAiModelAvailabilityState.NotInstalled)]
    public void MissingModel_KeepsRetrySetupAndDoesNotOfferChangeModel(
        LocalAiModelAvailabilityState modelState)
    {
        using var harness = new LocalAiHarness(modelState);

        Assert.False(harness.ViewModel.CanChangeModel);
        Assert.False(harness.ViewModel.HasInstalledModel);
        Assert.True(harness.ViewModel.CanRetrySetup);

        Assert.False(harness.ViewModel.ChangeModel());
        Assert.True(harness.ViewModel.RetrySetup());

        Assert.Equal(0, harness.Commands.ShowGatewayWizardCount);
        Assert.Equal(1, harness.Commands.ShowOnboardingCount);
    }

    [Fact]
    public async Task InstalledModel_RemainsVisibleButCannotOpenSetupDuringRuntimeAction()
    {
        using var harness = new LocalAiHarness(
            LocalAiModelAvailabilityState.Verified,
            LocalAiRuntimeState.Stopped);
        harness.Runtime.BlockStart();

        Task<bool> startTask = harness.ViewModel.StartAsync();

        Assert.True(harness.ViewModel.HasInstalledModel);
        Assert.False(harness.ViewModel.CanChangeModel);
        Assert.False(harness.ViewModel.ChangeModel());
        Assert.Equal(0, harness.Commands.ShowOnboardingCount);

        harness.Runtime.CompleteStart();
        Assert.True(await startTask);
    }

    [Fact]
    public void LocalAiPage_ChangeModelActionIsLocalizedAccessibleAndWired()
    {
        string root = TestRepositoryPaths.GetRepositoryRoot();
        string pageDirectory = Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Pages");
        string xaml = File.ReadAllText(Path.Combine(pageDirectory, "LocalAiPage.xaml"));
        string codeBehind = File.ReadAllText(Path.Combine(pageDirectory, "LocalAiPage.xaml.cs"));

        Assert.Contains("x:Uid=\"LocalAiPage_ChangeModelButton\"", xaml);
        Assert.Contains("x:Uid=\"LocalAiPage_ChangeModelDescription\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"LocalAiChangeModel\"", xaml);
        Assert.Contains("Click=\"OnChangeModel\"", xaml);
        Assert.Contains("_viewModel?.ChangeModel()", codeBehind);
    }

    private sealed class LocalAiHarness : IDisposable
    {
        private readonly FakeLocalAiRuntime _runtime;
        private readonly PermissionsPageRuntimeSource _gatewaySource;
        private readonly RecordingUiDispatcher _dispatcher;

        public LocalAiHarness(
            LocalAiModelAvailabilityState modelState,
            LocalAiRuntimeState runtimeState = LocalAiRuntimeState.Healthy)
        {
            _runtime = new FakeLocalAiRuntime(CreateSnapshot(modelState, runtimeState));
            var gatewayHost = new FakePermissionsPageRuntimeHost();
            _gatewaySource = new PermissionsPageRuntimeSource(gatewayHost);
            Commands = new FakeAppCommands();
            _dispatcher = new RecordingUiDispatcher();
            ViewModel = new LocalAiPageViewModel(
                _runtime,
                _gatewaySource,
                Commands,
                _dispatcher,
                new FixedHardwareProbe(CreateQualifiedHardware()));
        }

        public FakeAppCommands Commands { get; }
        public FakeLocalAiRuntime Runtime => _runtime;
        public LocalAiPageViewModel ViewModel { get; }

        public void Dispose()
        {
            ViewModel.Dispose();
            Commands.Dispose();
            _gatewaySource.Dispose();
            _dispatcher.Dispose();
            _runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        private static LocalAiRuntimeSnapshot CreateSnapshot(
            LocalAiModelAvailabilityState modelState,
            LocalAiRuntimeState runtimeState)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            LocalAiModelEvidence evidence = modelState switch
            {
                LocalAiModelAvailabilityState.Verified => new(
                    modelState,
                    now,
                    new string('a', 64),
                    1024),
                LocalAiModelAvailabilityState.Loaded => new(
                    modelState,
                    now,
                    new string('a', 64),
                    1024,
                    "test-model"),
                LocalAiModelAvailabilityState.NotInstalled => LocalAiModelEvidence.NotInstalled(now),
                _ => LocalAiModelEvidence.Unknown(now),
            };

            return new LocalAiRuntimeSnapshot(
                modelState is LocalAiModelAvailabilityState.Verified or LocalAiModelAvailabilityState.Loaded
                    ? runtimeState
                    : LocalAiRuntimeState.NotInstalled,
                modelState is LocalAiModelAvailabilityState.Verified or LocalAiModelAvailabilityState.Loaded
                    ? LocalAiOwnership.CompanionManaged
                    : LocalAiOwnership.None,
                new Uri("http://127.0.0.1:11983"),
                "test",
                "test-model",
                evidence,
                null,
                null,
                null,
                now);
        }
    }

    [Fact]
    public async Task UnsupportedHardware_KeepsExistingRuntimeManagementAvailable()
    {
        var runtime = new FakeLocalAiRuntime(CreateInstalledSnapshot());
        var runtimeHost = new FakePermissionsPageRuntimeHost
        {
            ConnectionSnapshot = GatewayConnectionSnapshot.Idle with
            {
                OperatorState = RoleConnectionState.Idle,
            },
        };
        using var gatewaySource = new PermissionsPageRuntimeSource(runtimeHost);
        var commands = new FakeAppCommands();
        using var viewModel = new LocalAiPageViewModel(
            runtime,
            gatewaySource,
            commands,
            new RecordingUiDispatcher(),
            new FixedHardwareProbe(HostHardwareInfo.Unknown));

        Assert.False(viewModel.IsAvailabilityKnown);
        Assert.True(viewModel.IsSetupAvailable);

        await ActivateAndWaitForAvailabilityAsync(viewModel);

        Assert.True(viewModel.IsAvailabilityKnown);
        Assert.False(viewModel.IsLocalAiAvailable);
        Assert.False(viewModel.IsSetupAvailable);
        Assert.Contains("NVIDIA GPU", viewModel.LocalAiUnavailableReason);
        Assert.False(viewModel.CanStart);
        Assert.True(viewModel.CanStop);
        Assert.True(viewModel.CanRestart);
        Assert.True(viewModel.CanOpenLogs);
        Assert.False(viewModel.CanRetrySetup);
        Assert.True(viewModel.CanRepairConnection);
        Assert.False(viewModel.CanOpenChat);
        Assert.True(await viewModel.StopAsync());
        Assert.True(await viewModel.RestartAsync());
        Assert.True(viewModel.OpenLogs());
        Assert.False(viewModel.RetrySetup());
        Assert.True(viewModel.RepairConnection());
        Assert.False(viewModel.OpenChat());
        Assert.Equal(1, commands.OpenLocalAiLogsCount);
        Assert.Equal(0, commands.ShowOnboardingCount);
        Assert.Equal(1, commands.ReconnectCount);
        Assert.Equal(0, commands.ShowChatCount);
        Assert.Equal(1, runtime.StopCount);
        Assert.Equal(1, runtime.RestartCount);
    }

    [Fact]
    public async Task UnsupportedHardware_KeepsChatAvailableForHealthyConnectedRuntime()
    {
        var runtime = new FakeLocalAiRuntime(CreateInstalledSnapshot());
        var runtimeHost = new FakePermissionsPageRuntimeHost
        {
            ConnectionSnapshot = GatewayConnectionSnapshot.Idle with
            {
                OperatorState = RoleConnectionState.Connected,
            },
        };
        using var gatewaySource = new PermissionsPageRuntimeSource(runtimeHost);
        var commands = new FakeAppCommands();
        using var viewModel = new LocalAiPageViewModel(
            runtime,
            gatewaySource,
            commands,
            new RecordingUiDispatcher(),
            new FixedHardwareProbe(HostHardwareInfo.Unknown));

        await ActivateAndWaitForAvailabilityAsync(viewModel);

        Assert.False(viewModel.IsLocalAiAvailable);
        Assert.True(viewModel.CanOpenChat);
        Assert.True(viewModel.OpenChat());
        Assert.Equal(1, commands.ShowChatCount);
    }

    [Fact]
    public async Task UnsupportedHardware_KeepsInstalledStoppedRuntimeStartAvailable()
    {
        var runtime = new FakeLocalAiRuntime(CreateInstalledSnapshot(LocalAiRuntimeState.Stopped));
        using var gatewaySource = new PermissionsPageRuntimeSource(new FakePermissionsPageRuntimeHost());
        using var viewModel = new LocalAiPageViewModel(
            runtime,
            gatewaySource,
            new FakeAppCommands(),
            new RecordingUiDispatcher(),
            new FixedHardwareProbe(HostHardwareInfo.Unknown));

        await ActivateAndWaitForAvailabilityAsync(viewModel);

        Assert.False(viewModel.IsSetupAvailable);
        Assert.True(viewModel.CanStart);
        Assert.True(await viewModel.StartAsync());
        Assert.Equal(1, runtime.StartCount);
    }

    [Fact]
    public async Task UnsupportedHardware_BlocksFreshSetupRetry()
    {
        var runtime = new FakeLocalAiRuntime(LocalAiRuntimeSnapshot.Initial(
            new Uri("http://127.0.0.1:18080"),
            DateTimeOffset.UtcNow));
        using var gatewaySource = new PermissionsPageRuntimeSource(new FakePermissionsPageRuntimeHost());
        using var viewModel = new LocalAiPageViewModel(
            runtime,
            gatewaySource,
            new FakeAppCommands(),
            new RecordingUiDispatcher(),
            new FixedHardwareProbe(HostHardwareInfo.Unknown));

        await ActivateAndWaitForAvailabilityAsync(viewModel);

        Assert.False(viewModel.IsSetupAvailable);
        Assert.False(viewModel.CanStart);
        Assert.False(viewModel.CanRetrySetup);
    }

    [Fact]
    public async Task QualifiedHardware_EnablesApplicableOptionsAndRoutesActions()
    {
        var runtime = new FakeLocalAiRuntime(CreateInstalledSnapshot());
        var runtimeHost = new FakePermissionsPageRuntimeHost
        {
            ConnectionSnapshot = GatewayConnectionSnapshot.Idle with
            {
                OperatorState = RoleConnectionState.Connected,
            },
        };
        using var gatewaySource = new PermissionsPageRuntimeSource(runtimeHost);
        var commands = new FakeAppCommands();
        using var viewModel = new LocalAiPageViewModel(
            runtime,
            gatewaySource,
            commands,
            new RecordingUiDispatcher(),
            new FixedHardwareProbe(CreateQualifiedHardware()));

        await ActivateAndWaitForAvailabilityAsync(viewModel);

        Assert.True(viewModel.IsLocalAiAvailable);
        Assert.True(viewModel.IsSetupAvailable);
        Assert.Null(viewModel.LocalAiUnavailableReason);
        Assert.True(viewModel.CanStop);
        Assert.True(viewModel.CanRestart);
        Assert.True(viewModel.CanOpenLogs);
        Assert.True(viewModel.CanOpenChat);
        Assert.True(viewModel.OpenLogs());
        Assert.True(viewModel.OpenChat());
        Assert.Equal(1, commands.OpenLocalAiLogsCount);
        Assert.Equal(1, commands.ShowChatCount);
    }

    private static async Task ActivateAndWaitForAvailabilityAsync(LocalAiPageViewModel viewModel)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.PropertyChanged += (_, _) =>
        {
            if (viewModel.IsAvailabilityKnown)
                completion.TrySetResult();
        };

        viewModel.Activate(null);
        if (!viewModel.IsAvailabilityKnown)
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static HostHardwareInfo CreateQualifiedHardware() =>
        new(
            Architecture.X64,
            TotalPhysicalMemoryBytes: 256_000_000_000,
            AvailablePhysicalMemoryBytes: 128_000_000_000,
            Gpus:
            [
                new GpuInfo(
                    GpuVendor.Nvidia,
                    "NVIDIA Test GPU",
                    GpuVisibleMemoryBytes: 128_000_000_000,
                    FreeGpuVisibleMemoryBytes: 128_000_000_000,
                    DriverVersion: "620.0",
                    CudaMajorVersion: 13,
                    StableId: "GPU-test"),
            ],
            VulkanAvailable: false);

    private static LocalAiRuntimeSnapshot CreateInstalledSnapshot(
        LocalAiRuntimeState state = LocalAiRuntimeState.Healthy)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string modelId = LocalModelCatalog.Models[0].Id;
        return new LocalAiRuntimeSnapshot(
            state,
            LocalAiOwnership.CompanionManaged,
            new Uri("http://127.0.0.1:18080"),
            "test",
            modelId,
            new LocalAiModelEvidence(
                LocalAiModelAvailabilityState.Verified,
                now,
                new string('0', 64),
                sizeBytes: 1),
            ProcessId: 1234,
            ProcessStartedAtUtc: now,
            Detail: null,
            UpdatedAtUtc: now);
    }

    private sealed class FixedHardwareProbe(HostHardwareInfo hardware) : IHostHardwareProbe
    {
        public HostHardwareInfo Probe() => hardware;
    }

    private sealed class FakeLocalAiRuntime(LocalAiRuntimeSnapshot snapshot) : ILocalAiRuntime
    {
        private TaskCompletionSource<LocalAiRuntimeSnapshot>? _startCompletion;

        public LocalAiRuntimeSnapshot Snapshot { get; private set; } = snapshot;
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public int RestartCount { get; private set; }
        public event EventHandler<LocalAiRuntimeSnapshotChangedEventArgs>? StateChanged
        {
            add { }
            remove { }
        }

        public void BlockStart() =>
            _startCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void CompleteStart() => _startCompletion?.TrySetResult(Snapshot);

        public Task<LocalAiRuntimeSnapshot> EnsureStartedAsync(CancellationToken cancellationToken = default)
        {
            StartCount++;
            return _startCompletion?.Task ?? Task.FromResult(Snapshot);
        }

        public Task<LocalAiRuntimeSnapshot> StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            return Task.FromResult(Snapshot);
        }

        public Task<LocalAiRuntimeSnapshot> RestartAsync(CancellationToken cancellationToken = default)
        {
            RestartCount++;
            return Task.FromResult(Snapshot);
        }

        public Task<LocalAiRuntimeSnapshot> RefreshAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Snapshot);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
