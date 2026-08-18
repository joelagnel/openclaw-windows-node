using Microsoft.Win32.SafeHandles;
using OpenClaw.Shared;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OpenClaw.Connection.LocalAi;

internal sealed record LocalAiProcessStartSpec(
    string ExecutablePath,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    string StandardOutputLogPath,
    string StandardErrorLogPath,
    long MaxLogBytes,
    int LogBackupCount,
    int MaxLogLineCharacters);

internal interface ILocalAiManagedProcess : IAsyncDisposable
{
    int ProcessId { get; }
    DateTimeOffset StartedAtUtc { get; }
    bool HasExited { get; }
    Task StopAsync(TimeSpan timeout, CancellationToken cancellationToken);
}

internal interface ILocalAiRuntimePlatform
{
    DateTimeOffset UtcNow { get; }
    WindowsTcpListenerSnapshotResult CaptureListeners();
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
    Task<ILocalAiManagedProcess> StartProcessAsync(
        LocalAiProcessStartSpec spec,
        Action<int?> exited,
        CancellationToken cancellationToken);
}

internal sealed class WindowsLocalAiRuntimePlatform(IOpenClawLogger logger) : ILocalAiRuntimePlatform
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public WindowsTcpListenerSnapshotResult CaptureListeners() => WindowsTcpListenerSnapshot.Capture();
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);

    public Task<ILocalAiManagedProcess> StartProcessAsync(
        LocalAiProcessStartSpec spec,
        Action<int?> exited,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Managed Ollama is supported only on Windows.");

        Directory.CreateDirectory(spec.WorkingDirectory);
        var stdout = new BoundedRotatingLogWriter(
            spec.StandardOutputLogPath, spec.MaxLogBytes, spec.LogBackupCount, spec.MaxLogLineCharacters, logger);
        var stderr = new BoundedRotatingLogWriter(
            spec.StandardErrorLogPath, spec.MaxLogBytes, spec.LogBackupCount, spec.MaxLogLineCharacters, logger);
        var startInfo = new ProcessStartInfo(spec.ExecutablePath, "serve")
        {
            WorkingDirectory = spec.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var pair in spec.Environment)
            startInfo.Environment[pair.Key] = pair.Value;

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = false };
        SafeJobHandle? job = null;
        try
        {
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.WriteLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.WriteLine(e.Data); };
            if (!process.Start())
                throw new InvalidOperationException("Ollama did not start.");

            job = WindowsJob.CreateKillOnClose();
            if (!AssignProcessToJobObject(job, process.SafeHandle))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not assign Ollama to its lifecycle job.");

            var managed = new WindowsManagedProcess(process, job, stdout, stderr);
            job = null;
            process.Exited += (_, _) =>
            {
                int? exitCode = null;
                try { exitCode = process.ExitCode; } catch { }
                exited(exitCode);
            };
            process.EnableRaisingEvents = true;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return Task.FromResult<ILocalAiManagedProcess>(managed);
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            job?.Dispose();
            process.Dispose();
            stdout.Dispose();
            stderr.Dispose();
            throw;
        }
    }

    private sealed class WindowsManagedProcess(
        Process process,
        SafeJobHandle job,
        BoundedRotatingLogWriter stdout,
        BoundedRotatingLogWriter stderr) : ILocalAiManagedProcess
    {
        private int _disposed;
        public int ProcessId => process.Id;
        public DateTimeOffset StartedAtUtc => process.StartTime.ToUniversalTime();
        public bool HasExited { get { try { return process.HasExited; } catch { return true; } } }

        public async Task StopAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (HasExited)
                return;
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { return; }
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            try { await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            try { await StopAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false); } catch { }
            job.Dispose();
            process.Dispose();
            stdout.Dispose();
            stderr.Dispose();
        }
    }

    private static class WindowsJob
    {
        public static SafeJobHandle CreateKillOnClose()
        {
            var job = CreateJobObjectW(IntPtr.Zero, null);
            if (job.IsInvalid)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not create the Ollama lifecycle job.");

            var limits = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = 0x00002000 },
            };
            var size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
            var pointer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(limits, pointer, false);
                if (!SetInformationJobObject(job, 9, pointer, (uint)size))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not configure the Ollama lifecycle job.");
                return job;
            }
            catch
            {
                job.Dispose();
                throw;
            }
            finally { Marshal.FreeHGlobal(pointer); }
        }
    }

    private sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeJobHandle() : base(ownsHandle: true) { }
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeJobHandle CreateJobObjectW(IntPtr securityAttributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeJobHandle job, int informationClass, IntPtr information, uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeJobHandle job, SafeProcessHandle process);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}

internal sealed class BoundedRotatingLogWriter : IDisposable
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly long _maxBytes;
    private readonly int _backupCount;
    private readonly int _maxLineCharacters;
    private readonly IOpenClawLogger _logger;
    private bool _disposed;

    public BoundedRotatingLogWriter(string path, long maxBytes, int backupCount, int maxLineCharacters, IOpenClawLogger logger)
    {
        _path = path;
        _maxBytes = Math.Max(1024, maxBytes);
        _backupCount = Math.Clamp(backupCount, 0, 10);
        _maxLineCharacters = Math.Max(256, maxLineCharacters);
        _logger = logger;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    }

    public void WriteLine(string line)
    {
        lock (_gate)
        {
            if (_disposed) return;
            try
            {
                var sanitized = TokenSanitizer.SanitizeLogMessage(line);
                if (sanitized.Length > _maxLineCharacters)
                    sanitized = sanitized[.._maxLineCharacters] + " [truncated]";
                var newlineBytes = System.Text.Encoding.UTF8.GetByteCount(Environment.NewLine);
                var allowedBytes = checked((int)Math.Min(int.MaxValue, _maxBytes - newlineBytes));
                while (System.Text.Encoding.UTF8.GetByteCount(sanitized) > allowedBytes && sanitized.Length > 1)
                    sanitized = sanitized[..Math.Max(1, sanitized.Length * 3 / 4)];
                var bytes = System.Text.Encoding.UTF8.GetByteCount(sanitized) + newlineBytes;
                if ((File.Exists(_path) ? new FileInfo(_path).Length : 0) + bytes > _maxBytes)
                    Rotate();
                File.AppendAllText(_path, sanitized + Environment.NewLine, System.Text.Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.Warn($"Could not write the managed Ollama log: {ex.Message}");
            }
        }
    }

    private void Rotate()
    {
        if (_backupCount == 0) { File.Delete(_path); return; }
        File.Delete(_path + "." + _backupCount);
        for (var i = _backupCount - 1; i >= 1; i--)
        {
            var source = _path + "." + i;
            if (File.Exists(source)) File.Move(source, _path + "." + (i + 1), overwrite: true);
        }
        if (File.Exists(_path)) File.Move(_path, _path + ".1", overwrite: true);
    }

    public void Dispose() { lock (_gate) _disposed = true; }
}
