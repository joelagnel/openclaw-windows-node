using OpenClaw.Connection;

namespace OpenClaw.SetupEngine;

/// <summary>
/// Detects existing local gateway configuration to show accurate replacement summaries.
/// </summary>
public sealed class ExistingConfigDetector
{
    /// <summary>How long the WSL distro probe may run before it is abandoned.</summary>
    private const int WslProbeTimeoutMs = 5000;

    /// <summary>Grace period for the stdout pipe to drain once wsl.exe has exited.</summary>
    private const int WslProbeDrainTimeoutMs = 1000;

    public sealed record ExistingConfig(
        bool HasLocalGateway,
        string? LocalGatewayId,
        string? LocalGatewayUrl,
        bool HasDistro,
        string? DistroName,
        bool HasIdentityFiles,
        int PreservedGatewayCount,
        IReadOnlyList<string> PreservedGatewayNames);

    /// <summary>
    /// Detect existing local configuration by checking the gateway registry and WSL distros.
    /// </summary>
    public static ExistingConfig Detect(string dataDir, string targetDistroName)
    {
        var registry = new GatewayRegistry(dataDir);
        registry.Load();
        var all = registry.GetAll();

        var localRecord = all.FirstOrDefault(r => r.IsLocal && r.SshTunnel == null);
        var preserved = all.Where(r => !r.IsLocal || r.SshTunnel != null).ToList();

        var hasDistro = false;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("wsl.exe", "--list --quiet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc != null)
            {
                // Drain both pipes asynchronously so every wait here is bounded.
                // Reading stdout to end synchronously blocks until the last handle on
                // the write end closes, which can outlive wsl.exe when it leaves a
                // helper process behind, and that read accepts no timeout. The
                // WaitForExit budget below never applied to it, so a wedged WSL stack
                // froze setup indefinitely instead of falling back to "no distro".
                var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                ObserveFailure(proc.StandardError.ReadToEndAsync());

                var exited = proc.WaitForExit(WslProbeTimeoutMs);
                if (!exited)
                    TryKillProcessTree(proc);

                if (exited && stdoutTask.Wait(WslProbeDrainTimeoutMs))
                {
                    hasDistro = WslInstallSupport.ContainsDistro(stdoutTask.Result, targetDistroName);
                }
                else
                {
                    ObserveFailure(stdoutTask);
                    System.Diagnostics.Debug.WriteLine(
                        $"WSL distro detection did not finish within {WslProbeTimeoutMs} ms; " +
                        $"assuming distro '{targetDistroName}' is absent.");
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WSL distro detection failed: {ex.Message}");
        }

        var hasIdentity = false;
        if (localRecord != null)
        {
            var identityDir = registry.GetIdentityDirectory(localRecord.Id);
            hasIdentity = Directory.Exists(identityDir) && Directory.EnumerateFiles(identityDir).Any();
        }

        return new ExistingConfig(
            HasLocalGateway: localRecord != null,
            LocalGatewayId: localRecord?.Id,
            LocalGatewayUrl: localRecord?.Url,
            HasDistro: hasDistro,
            DistroName: hasDistro ? targetDistroName : null,
            HasIdentityFiles: hasIdentity,
            PreservedGatewayCount: preserved.Count,
            PreservedGatewayNames: preserved.Select(r => r.FriendlyName ?? r.Url).ToList());
    }

    /// <summary>
    /// Stop an unresponsive probe so it cannot keep holding the redirected pipes.
    /// </summary>
    private static void TryKillProcessTree(System.Diagnostics.Process proc)
    {
        try
        {
            if (!proc.HasExited)
                proc.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Stopping the WSL detection process failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Keep an abandoned pipe read from surfacing later as an unobserved task fault.
    /// </summary>
    private static void ObserveFailure(Task task) =>
        _ = task.ContinueWith(
            static completed => System.Diagnostics.Debug.WriteLine(
                $"WSL distro detection stream read failed: {completed.Exception?.GetBaseException().Message}"),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    /// <summary>
    /// Build a human-readable summary of what will happen during setup.
    /// </summary>
    public static string BuildReplacementSummary(ExistingConfig config)
    {
        if (!config.HasLocalGateway && !config.HasDistro)
            return "A new local WSL gateway will be created. No existing configuration will be affected.";

        var lines = new List<string>();

        if (config.HasDistro)
            lines.Add($"• WSL distro '{config.DistroName}' will be deleted and recreated");
        if (config.HasLocalGateway)
            lines.Add("• Local gateway record will be replaced");
        if (config.HasIdentityFiles)
            lines.Add("• Device identity files for the local gateway will be regenerated");

        if (config.PreservedGatewayCount > 0)
        {
            lines.Add(string.Empty);
            lines.Add($"The following {config.PreservedGatewayCount} gateway(s) will NOT be affected:");
            foreach (var name in config.PreservedGatewayNames)
                lines.Add($"  • {name}");
        }

        return string.Join("\n", lines);
    }
}
