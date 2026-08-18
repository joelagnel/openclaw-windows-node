namespace OpenClaw.Connection.LocalAi;

public enum LocalAiRuntimeState
{
    NotInstalled,
    Stopped,
    Starting,
    Healthy,
    Stopping,
    Conflict,
    Failed,
}

public enum LocalAiOwnership
{
    None,
    Managed,
    External,
}

/// <summary>
/// Evidence-backed availability of the exact managed manifest model tag.
/// Unknown means no current qualified API evidence exists.
/// </summary>
public enum LocalAiModelAvailabilityState
{
    Unknown,
    NotInstalled,
    Downloaded,
    Loaded,
}

public sealed record LocalAiRuntimeSnapshot(
    LocalAiRuntimeState State,
    LocalAiOwnership Ownership,
    Uri Endpoint,
    string? EngineVersion,
    string? ModelTag,
    LocalAiModelAvailabilityState ModelAvailability,
    int? ProcessId,
    DateTimeOffset? ProcessStartedAtUtc,
    string? Detail,
    DateTimeOffset UpdatedAtUtc)
{
    public static LocalAiRuntimeSnapshot Initial(Uri endpoint, DateTimeOffset now) =>
        new(
            LocalAiRuntimeState.Stopped,
            LocalAiOwnership.None,
            endpoint,
            null,
            null,
            LocalAiModelAvailabilityState.Unknown,
            null,
            null,
            null,
            now);
}

public sealed class LocalAiRuntimeSnapshotChangedEventArgs(LocalAiRuntimeSnapshot snapshot) : EventArgs
{
    public LocalAiRuntimeSnapshot Snapshot { get; } = snapshot;
}

public interface ILocalAiRuntime : IAsyncDisposable
{
    LocalAiRuntimeSnapshot Snapshot { get; }
    event EventHandler<LocalAiRuntimeSnapshotChangedEventArgs>? StateChanged;
    Task<LocalAiRuntimeSnapshot> EnsureStartedAsync(CancellationToken cancellationToken = default);
    Task<LocalAiRuntimeSnapshot> StopAsync(CancellationToken cancellationToken = default);
    Task<LocalAiRuntimeSnapshot> RestartAsync(CancellationToken cancellationToken = default);
    Task<LocalAiRuntimeSnapshot> RefreshAsync(CancellationToken cancellationToken = default);
}

public sealed record OllamaRuntimeOptions
{
    public required LocalAiPaths Paths { get; init; }
    public Uri Endpoint { get; init; } = new("http://127.0.0.1:11434");
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan HealthPollInterval { get; init; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public int MaxRestartAttempts { get; init; } = 2;
    public TimeSpan RestartDelay { get; init; } = TimeSpan.FromSeconds(1);
    public int ContextLength { get; init; } = 262_144;
    public bool FlashAttention { get; init; } = true;
    public string KvCacheType { get; init; } = "f16";
    public int NumParallel { get; init; } = 1;
    public int MaxLoadedModels { get; init; } = 1;
    public TimeSpan KeepAlive { get; init; } = TimeSpan.FromMinutes(10);
    public string LlmLibrary { get; init; } = "cuda_v13";
    public long MaxLogBytes { get; init; } = 4 * 1024 * 1024;
    public int LogBackupCount { get; init; } = 2;
    public int MaxLogLineCharacters { get; init; } = 16 * 1024;
}
