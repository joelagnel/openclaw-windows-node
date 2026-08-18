using OpenClaw.Connection;
using OpenClaw.Connection.LocalAi;
using OpenClawTray.Services;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace OpenClawTray.Presentation;

internal enum LocalAiEnginePresentationState
{
    Running,
    Stopped,
    Error,
}

internal enum LocalAiModelPresentationState
{
    Unknown,
    NotInstalled,
    Downloaded,
    Loaded,
}

internal enum LocalAiGatewayPresentationState
{
    Connected,
    Connecting,
    NeedsAttention,
    Disconnected,
    Error,
}

/// <summary>
/// WinUI-free presentation and action owner for the Local AI Hub page. Native process,
/// listener, health, and log mechanics remain behind <see cref="ILocalAiRuntime"/>.
/// </summary>
internal sealed class LocalAiPageViewModel : INavigationAware, IDisposable, INotifyPropertyChanged
{
    private readonly ILocalAiRuntime _runtime;
    private readonly IPermissionsPageRuntimeSource _gatewaySource;
    private readonly IAppCommands _appCommands;
    private readonly IUiDispatcher _dispatcher;
    private LocalAiRuntimeSnapshot _runtimeSnapshot;
    private GatewayConnectionSnapshot _gatewaySnapshot;
    private bool _subscribed;
    private bool _disposed;
    private bool _isBusy;
    private string? _actionError;
    private CancellationTokenSource? _refreshCancellation;

    public LocalAiPageViewModel(
        ILocalAiRuntime runtime,
        IPermissionsPageRuntimeSource gatewaySource,
        IAppCommands appCommands,
        IUiDispatcher dispatcher)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _gatewaySource = gatewaySource ?? throw new ArgumentNullException(nameof(gatewaySource));
        _appCommands = appCommands ?? throw new ArgumentNullException(nameof(appCommands));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _runtimeSnapshot = runtime.Snapshot;
        _gatewaySnapshot = gatewaySource.Current.ConnectionSnapshot;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal ILocalAiRuntime Runtime => _runtime;
    internal bool IsActive { get; private set; }
    internal bool IsDisposed => _disposed;

    public LocalAiEnginePresentationState EngineState => _runtimeSnapshot.State switch
    {
        LocalAiRuntimeState.Healthy => LocalAiEnginePresentationState.Running,
        LocalAiRuntimeState.Conflict or LocalAiRuntimeState.Failed => LocalAiEnginePresentationState.Error,
        _ => LocalAiEnginePresentationState.Stopped,
    };

    public string EngineStatusResourceKey => EngineState switch
    {
        LocalAiEnginePresentationState.Running => "LocalAiPage_Engine_Running",
        LocalAiEnginePresentationState.Error => "LocalAiPage_Engine_Error",
        _ => "LocalAiPage_Engine_Stopped",
    };

    public string EngineOwnershipResourceKey => _runtimeSnapshot.Ownership switch
    {
        LocalAiOwnership.External => "LocalAiPage_Engine_External",
        LocalAiOwnership.Managed => "LocalAiPage_Engine_Managed",
        _ when HasManagedInstall => "LocalAiPage_Engine_Managed",
        _ => "LocalAiPage_Engine_NotManaged",
    };

    public string? EngineVersion => _runtimeSnapshot.EngineVersion;
    public string? EngineDetail => _runtimeSnapshot.Detail;
    public string? ModelTag => _runtimeSnapshot.ModelTag;
    public const string ContextLengthText = "256K";
    public string KvCacheText => IsExternal ? string.Empty : "FP16";
    public string? KvCacheTextResourceKey => IsExternal ? "LocalAiPage_Value_ExternalSettings" : null;

    public LocalAiModelPresentationState ModelState => _runtimeSnapshot.ModelAvailability switch
    {
        LocalAiModelAvailabilityState.NotInstalled => LocalAiModelPresentationState.NotInstalled,
        LocalAiModelAvailabilityState.Downloaded => LocalAiModelPresentationState.Downloaded,
        LocalAiModelAvailabilityState.Loaded => LocalAiModelPresentationState.Loaded,
        _ => LocalAiModelPresentationState.Unknown,
    };

    public string ModelStatusResourceKey => ModelState switch
    {
        LocalAiModelPresentationState.NotInstalled => "LocalAiPage_Model_NotInstalled",
        LocalAiModelPresentationState.Downloaded => "LocalAiPage_Model_Downloaded",
        LocalAiModelPresentationState.Loaded => "LocalAiPage_Model_Loaded",
        _ => "LocalAiPage_Model_Unknown",
    };

    public LocalAiGatewayPresentationState GatewayState => _gatewaySnapshot.OperatorState switch
    {
        RoleConnectionState.Connected => LocalAiGatewayPresentationState.Connected,
        RoleConnectionState.Connecting => LocalAiGatewayPresentationState.Connecting,
        RoleConnectionState.PairingRequired => LocalAiGatewayPresentationState.NeedsAttention,
        RoleConnectionState.Error or RoleConnectionState.PairingRejected or RoleConnectionState.RateLimited =>
            LocalAiGatewayPresentationState.Error,
        _ => LocalAiGatewayPresentationState.Disconnected,
    };

    public string GatewayStatusResourceKey => GatewayState switch
    {
        LocalAiGatewayPresentationState.Connected => "LocalAiPage_Gateway_Connected",
        LocalAiGatewayPresentationState.Connecting => "LocalAiPage_Gateway_Connecting",
        LocalAiGatewayPresentationState.NeedsAttention => "LocalAiPage_Gateway_NeedsAttention",
        LocalAiGatewayPresentationState.Error => "LocalAiPage_Gateway_Error",
        _ => "LocalAiPage_Gateway_Disconnected",
    };

    public string? GatewayDetail => _gatewaySnapshot.GatewayName ?? _gatewaySnapshot.GatewayUrl;
    public string? ActionError => _actionError;
    public bool IsBusy => _isBusy;
    public bool IsExternal => _runtimeSnapshot.Ownership == LocalAiOwnership.External;
    public bool CanStart => !IsBusy && HasManagedInstall && !IsExternal &&
        _runtimeSnapshot.State is LocalAiRuntimeState.Stopped or LocalAiRuntimeState.Failed;
    public bool CanStop => !IsBusy && _runtimeSnapshot.Ownership == LocalAiOwnership.Managed &&
        _runtimeSnapshot.State is LocalAiRuntimeState.Starting or LocalAiRuntimeState.Healthy;
    public bool CanRestart => !IsBusy && _runtimeSnapshot.Ownership == LocalAiOwnership.Managed &&
        _runtimeSnapshot.State == LocalAiRuntimeState.Healthy;
    public bool CanOpenLogs => !IsBusy && HasManagedInstall && !IsExternal;
    public bool CanRetrySetup => !IsBusy &&
        ModelState is LocalAiModelPresentationState.NotInstalled or LocalAiModelPresentationState.Unknown;
    public bool CanRepairConnection => !IsBusy && GatewayState is not (LocalAiGatewayPresentationState.Connected or LocalAiGatewayPresentationState.Connecting);
    public bool CanOpenChat => !IsBusy &&
        GatewayState == LocalAiGatewayPresentationState.Connected &&
        _runtimeSnapshot.State == LocalAiRuntimeState.Healthy &&
        (_runtimeSnapshot.Ownership switch
        {
            LocalAiOwnership.Managed =>
                ModelState is LocalAiModelPresentationState.Downloaded or LocalAiModelPresentationState.Loaded,
            LocalAiOwnership.External => ModelState != LocalAiModelPresentationState.NotInstalled,
            _ => false,
        });

    private bool HasManagedInstall =>
        _runtimeSnapshot.Ownership == LocalAiOwnership.Managed ||
        (_runtimeSnapshot.Ownership == LocalAiOwnership.None &&
         _runtimeSnapshot.State != LocalAiRuntimeState.NotInstalled &&
         !string.IsNullOrWhiteSpace(_runtimeSnapshot.ModelTag));

    public void Activate(object? parameter)
    {
        ThrowIfDisposed();
        IsActive = true;
        if (!_subscribed)
        {
            _runtime.StateChanged += OnRuntimeStateChanged;
            _gatewaySource.Changed += OnGatewayChanged;
            _subscribed = true;
        }

        ApplyRuntimeSnapshot(_runtime.Snapshot);
        ApplyGatewaySnapshot(_gatewaySource.Current.ConnectionSnapshot);
        StartRuntimeRefresh();
    }

    public void Deactivate()
    {
        CancelRuntimeRefresh();
        if (_subscribed)
        {
            _runtime.StateChanged -= OnRuntimeStateChanged;
            _gatewaySource.Changed -= OnGatewayChanged;
            _subscribed = false;
        }
        IsActive = false;
    }

    private void StartRuntimeRefresh()
    {
        CancelRuntimeRefresh();
        var cancellation = new CancellationTokenSource();
        _refreshCancellation = cancellation;
        _ = RefreshRuntimeSnapshotAsync(cancellation);
    }

    private void CancelRuntimeRefresh()
    {
        var cancellation = _refreshCancellation;
        _refreshCancellation = null;
        if (cancellation is null)
            return;
        cancellation.Cancel();
    }

    private async Task RefreshRuntimeSnapshotAsync(CancellationTokenSource cancellation)
    {
        try
        {
            var snapshot = await _runtime.RefreshAsync(cancellation.Token).ConfigureAwait(false);
            ApplyOnUiThread(() => ApplyRuntimeSnapshot(snapshot));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ApplyOnUiThread(() =>
            {
                _actionError = ex.Message;
                OnPropertyChanged(null);
            });
        }
        finally
        {
            if (ReferenceEquals(_refreshCancellation, cancellation))
                _refreshCancellation = null;
            cancellation.Dispose();
        }
    }

    public Task<bool> StartAsync() => RunRuntimeActionAsync(CanStart, _runtime.EnsureStartedAsync);
    public Task<bool> StopAsync() => RunRuntimeActionAsync(CanStop, _runtime.StopAsync);
    public Task<bool> RestartAsync() => RunRuntimeActionAsync(CanRestart, _runtime.RestartAsync);

    public bool OpenLogs()
    {
        if (!CanOpenLogs)
            return false;
        _appCommands.OpenLocalAiLogs();
        return true;
    }

    public bool RepairConnection()
    {
        if (!CanRepairConnection)
            return false;
        _appCommands.Reconnect();
        return true;
    }

    public bool OpenChat()
    {
        if (!CanOpenChat)
            return false;
        _appCommands.ShowChat();
        return true;
    }

    public bool RetrySetup()
    {
        if (!CanRetrySetup)
            return false;
        _appCommands.ShowOnboarding();
        return true;
    }

    private async Task<bool> RunRuntimeActionAsync(
        bool isAllowed,
        Func<CancellationToken, Task<LocalAiRuntimeSnapshot>> action)
    {
        ThrowIfDisposed();
        if (!isAllowed || _isBusy)
            return false;

        _isBusy = true;
        _actionError = null;
        OnPropertyChanged(null);
        try
        {
            var snapshot = await action(CancellationToken.None);
            ApplyRuntimeSnapshot(snapshot);
            return snapshot.State is not (LocalAiRuntimeState.Conflict or LocalAiRuntimeState.Failed);
        }
        catch (Exception ex)
        {
            _actionError = ex.Message;
            OnPropertyChanged(null);
            return false;
        }
        finally
        {
            _isBusy = false;
            OnPropertyChanged(null);
        }
    }

    private void OnRuntimeStateChanged(object? sender, LocalAiRuntimeSnapshotChangedEventArgs e) =>
        ApplyOnUiThread(() => ApplyRuntimeSnapshot(e.Snapshot));

    private void OnGatewayChanged(object? sender, PermissionsRuntimeSourceChangedEventArgs e) =>
        ApplyOnUiThread(() => ApplyGatewaySnapshot(e.Snapshot.ConnectionSnapshot));

    private void ApplyOnUiThread(Action action)
    {
        if (_disposed || !IsActive)
            return;
        if (_dispatcher.HasThreadAccess)
            action();
        else
            _dispatcher.TryEnqueue(() => { if (!_disposed && IsActive) action(); });
    }

    private void ApplyRuntimeSnapshot(LocalAiRuntimeSnapshot snapshot)
    {
        _runtimeSnapshot = snapshot;
        OnPropertyChanged(null);
    }

    private void ApplyGatewaySnapshot(GatewayConnectionSnapshot snapshot)
    {
        _gatewaySnapshot = snapshot;
        OnPropertyChanged(null);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        Deactivate();
        _disposed = true;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
