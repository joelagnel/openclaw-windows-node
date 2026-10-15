using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenClaw.Connection;
using OpenClaw.Shared;

namespace OpenClaw.SetupEngine;

/// <summary>Registers user-scoped cleanup that survives removal of the Companion MSIX.</summary>
public static class GatewayPackageCleanup
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFamilyName(ref uint length, StringBuilder family);

    public static async Task<bool> InitializeAsync(ICommandRunner commands, string dataDir, string localDataDir,
        string distroName, CancellationToken ct = default, Func<string, bool>? preserve = null)
    {
        if (!OperatingSystem.IsWindows()) return true;
        try
        {
            var directory = await RunAsync(commands, false, null, null, false, ct);
            if (directory is not null) await ReconcileAsync(directory, dataDir, localDataDir, ct);
            if (directory is not null && File.Exists(Path.Combine(directory, "owned.json")))
                await RunAsync(commands, true, null, null, false, ct);
            if (ManagedDistroOwnership.HasPathBoundMarkerEvidence(localDataDir, distroName) &&
                DistroInstallPathPolicy.TryGetManagedInstallPath(localDataDir, distroName, out var path, out _) &&
                ManagedDistroOwnership.HasRegisteredDistroEvidence(dataDir, localDataDir, distroName, path,
                    new WindowsWslRegistrationInspector(), out _))
            {
                // Adopt retained installations prospectively. Past uninstall receipts do not authorize removal.
                await RunAsync(commands, true, distroName, path, false, ct);
            }
            return true;
        }
        catch (Exception error) when (preserve is not null && error is (IOException or UnauthorizedAccessException or InvalidOperationException))
        {
            if (!preserve(error.Message)) return false;
            var directory = await RunAsync(commands, false, null, null, false, ct, preserve: true);
            if (directory is not null) await ReconcileAsync(directory, dataDir, localDataDir, ct);
            return true;
        }
    }

    public static Task RegisterAsync(ICommandRunner commands, string? distroName = null,
        string? installPath = null, bool ownNativePackage = false, CancellationToken ct = default) =>
        RunAsync(commands, true, distroName, installPath, ownNativePackage, ct, createdDistro: distroName is not null);

    private static async Task<string?> RunAsync(ICommandRunner commands, bool register, string? distroName,
        string? installPath, bool ownNativePackage, CancellationToken ct, bool preserve = false, bool createdDistro = false)
    {
        if (!OperatingSystem.IsWindows() ||
            Environment.GetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR") is { Length: > 0 }) return null;
        uint length = 256;
        var family = new StringBuilder((int)length);
        var result = GetCurrentPackageFamilyName(ref length, family);
        if (result == 15700) return null; // Unpackaged installations retain their existing uninstaller.
        if (result != 0) throw new InvalidOperationException("Companion package identity could not be read.");
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".openclaw-companion", "cleanup", family.ToString());
        if (!register && !preserve && !File.Exists(Path.Combine(directory, "owned.json"))) return directory;
        Directory.CreateDirectory(directory);
        var scriptPath = Path.Combine(directory, "cleanup.ps1");
        using var source = typeof(GatewayPackageCleanup).Assembly.GetManifestResourceStream(
            "OpenClaw.SetupEngine.GatewayPackageCleanup.ps1")
            ?? throw new InvalidOperationException("The Gateway cleanup helper is missing.");
        using var reader = new StreamReader(source);
        await AtomicFile.WriteAllTextAsync(scriptPath, await reader.ReadToEndAsync(ct), ct);
        var arguments = new List<string> { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
            "-File", scriptPath, "-Family", family.ToString() };
        if (register) arguments.Add("-Register");
        if (preserve) arguments.Add("-Preserve");
        if (createdDistro) arguments.Add("-CreatedDistro");
        if (distroName is not null) arguments.AddRange(["-DistroName", distroName, "-InstallPath", installPath!]);
        if (ownNativePackage) arguments.Add("-OwnNativePackage");
        var registration = await commands.RunAsync(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe"), arguments.ToArray(),
            TimeSpan.FromSeconds(register ? 30 : 180), ct: ct);
        if (registration.TimedOut || registration.ExitCode != 0)
        {
            var diagnostic = SetupLogger.Sanitize(registration.Stderr);
            throw new InvalidOperationException(register
                ? "Automatic Gateway cleanup could not be registered. " + diagnostic[..Math.Min(diagnostic.Length, 2048)]
                : "Previous managed Gateway cleanup remains pending. Reopen Companion to retry. " + diagnostic[..Math.Min(diagnostic.Length, 2048)]);
        }
        return directory;
    }

    private sealed record Removal(string? DistroName, string? InstallPath, string? NativeFamily);

    private static async Task ReconcileAsync(string directory, string dataDir, string localDataDir, CancellationToken ct)
    {
        var receipt = Path.Combine(directory, "cleanup-result.json");
        if (!File.Exists(receipt)) return;
        FileStream lease;
        for (var attempt = 0; ; attempt++)
        {
            try { lease = File.Open(Path.Combine(directory, "cleanup.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); break; }
            catch (IOException) when (attempt < 100) { await Task.Delay(100, ct); }
        }
        using (lease)
        {
            if (!File.Exists(receipt)) return;
            var removals = JsonSerializer.Deserialize<Removal[]>(File.ReadAllText(receipt))
                ?? throw new InvalidDataException("The Gateway cleanup receipt is invalid.");
            var registry = new GatewayRegistry(dataDir);
            registry.Load();
            var removed = registry.GetAll().Where(record => record.IsLocal && record.SshTunnel is null && removals.Any(
                removal => removal.DistroName is { Length: > 0 } distro && string.Equals(
                    GatewayRecordEditing.ResolveManagedDistroName(record), distro, StringComparison.OrdinalIgnoreCase) ||
                    removal.NativeFamily is { Length: > 0 } family && record.NativePackageFamilyName == family)).ToArray();
            foreach (var record in removed)
                if (!Guid.TryParse(record.Id, out _) || !DeviceIdentity.BeginClearAllDeviceTokens(registry.GetIdentityDirectory(record.Id)).Success)
                    throw new IOException("The removed Gateway's credentials could not be retired.");
            var settingsPath = Path.Combine(dataDir, "settings.json");
            var statePath = Path.Combine(localDataDir, "setup-state.json");
            JsonObject? state = null;
            try { if (File.Exists(statePath)) state = JsonNode.Parse(File.ReadAllText(statePath)) as JsonObject; }
            catch (JsonException) { } // Invalid state cannot bind legacy credentials to a removed Gateway.
            var retiredState = removals.Any(removal => removal.DistroName is { Length: > 0 } distro &&
                string.Equals(state?["DistroName"]?.GetValue<string>(), distro, StringComparison.OrdinalIgnoreCase) &&
                ManagedDistroOwnership.HasPathBoundMarkerEvidence(localDataDir, distro));
            using (PersistenceFileLease.Acquire(settingsPath))
            {
                if (File.Exists(settingsPath) && JsonNode.Parse(File.ReadAllText(settingsPath)) is JsonObject settings)
                {
                    var legacyUrl = settings["GatewayUrl"]?.GetValue<string>();
                    if (settings["UseSshTunnel"]?.GetValue<bool>() != true && (removed.Any(record => record.Url == legacyUrl ||
                        GatewayRecordEditing.AreEquivalentLoopbackEndpoints(record.Url, legacyUrl) ||
                        string.IsNullOrWhiteSpace(legacyUrl) && record.Id == registry.ActiveGatewayId) ||
                        retiredState && GatewayRecordEditing.AreEquivalentLoopbackEndpoints(legacyUrl, state?["GatewayUrl"]?.GetValue<string>())))
                    {
                        if (!DeviceIdentity.BeginClearAllDeviceTokens(dataDir).Success)
                            throw new IOException("The removed Gateway's legacy credentials could not be retired.");
                        settings["GatewayUrl"] = "";
                        settings.Remove("Token");
                        settings.Remove("BootstrapToken");
                    }
                    if (removed.Any(record => record.Id == settings["PreferredGatewayId"]?.GetValue<string>())) settings["PreferredGatewayId"] = null;
                    AtomicFile.WriteAllText(settingsPath, settings.ToJsonString(new() { WriteIndented = true }));
                }
            }
            foreach (var record in removed) registry.Remove(record.Id);
            if (removed.Length > 0) registry.Save();
            if (retiredState) File.Delete(statePath);
            foreach (var removal in removals)
                if (removal.DistroName is { Length: > 0 } distro && removal.InstallPath is { Length: > 0 } path &&
                    ManagedDistroOwnership.HasPathBoundMarkerEvidence(localDataDir, distro) &&
                    !ManagedDistroOwnership.DeleteMarker(localDataDir, distro, path))
                    throw new IOException("The removed Gateway's setup ownership could not be retired.");
            File.Delete(receipt);
        }
    }
}
