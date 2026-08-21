using System.Net.Http;
using System.Text.Json;

namespace OpenClaw.SetupEngine;

/// <summary>
/// The unauthenticated GitHub API quota, which <c>wsl --install</c> depends on.
/// </summary>
internal sealed record GitHubApiQuota(int Limit, int Remaining, DateTimeOffset ResetsAt)
{
    public bool IsExhausted => Remaining <= 0;

    public int Used => Math.Max(0, Limit - Remaining);
}

/// <summary>
/// Explains why an elevated <c>wsl --install</c> failed.
/// </summary>
/// <remarks>
/// <para>
/// <c>C:\Windows\System32\wsl.exe</c> is a stub; the real WSL ships out of band.
/// When WSL is absent, <c>wsl --install</c> resolves the package to download by
/// calling <c>https://api.github.com/repos/Microsoft/WSL/releases/latest</c> — that
/// URL is embedded in the stub, and no command-line option redirects it to the
/// Microsoft Store. Unauthenticated GitHub API calls are capped at 60/hour
/// <em>per IP</em>, so any machine behind shared egress (corporate NAT, VPN, cloud
/// hosts, CI, remote-accessed lab hardware) can find the quota already spent by
/// unrelated traffic. wsl.exe then prints "Forbidden (403)." and exits 1.
/// </para>
/// <para>
/// That output is unreachable to us: the installer must run elevated, elevation
/// requires ShellExecute, and ShellExecute cannot redirect stdout/stderr. So we
/// reconstruct the cause instead, from the same quota endpoint wsl.exe consumes.
/// <c>/rate_limit</c> is itself exempt from the quota, so probing is free.
/// </para>
/// </remarks>
internal static class WslPlatformInstallDiagnostics
{
    private const string RateLimitUrl = "https://api.github.com/rate_limit";

    /// <summary>The Microsoft Store product id for Windows Subsystem for Linux.</summary>
    private const string WslStoreProductId = "9P9TQF7MRM4R";

    private static readonly TimeSpan s_probeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Hands the user a way to install WSL that does not depend on OpenClaw
    /// succeeding. The Store route is listed first because it does not touch the
    /// GitHub API at all, so it works while the quota is still spent.
    /// </summary>
    /// <remarks>
    /// The failure surface renders this in a wrapping monospace card and turns the
    /// first URL into a clickable link, so the line breaks and the bare commands
    /// survive to the user.
    /// </remarks>
    public static string SelfInstallInstructions =>
        "Install WSL yourself, then run setup again:" + Environment.NewLine +
        $"  1. Microsoft Store (works even while the GitHub quota is spent): {WslInstallSupport.UpdateUrl}" +
        Environment.NewLine +
        $"     or run: winget install --id {WslStoreProductId} --source msstore" + Environment.NewLine +
        "  2. Or, in an elevated PowerShell: wsl --install --no-distribution" + Environment.NewLine +
        "Reboot if Windows asks for one.";

    /// <summary>
    /// Builds the operator-facing explanation for a failed platform install.
    /// Pure, so the wording is covered by tests.
    /// </summary>
    public static string DescribeFailure(int exitCode, GitHubApiQuota? quota)
    {
        var reason = quota is { IsExhausted: true }
            ? "wsl --install downloads WSL from GitHub, and this machine has already used its full " +
              $"unauthenticated GitHub API quota ({quota.Used}/{quota.Limit}), which resets at " +
              $"{quota.ResetsAt.ToLocalTime():HH:mm} local time. Shared networks reach that cap without " +
              "any help from OpenClaw."
            : "wsl --install downloads WSL from GitHub, and that download did not complete.";

        return $"WSL platform install failed with exit code {exitCode}. {reason}" +
            Environment.NewLine + Environment.NewLine + SelfInstallInstructions;
    }

    /// <summary>
    /// Explains a pre-launch abort, used when the quota is already spent and the
    /// install would only fail after prompting for administrator approval.
    /// </summary>
    public static string DescribeUnavailableDownload(GitHubApiQuota quota) =>
        "WSL is not installed, and OpenClaw cannot install it right now: wsl --install downloads WSL " +
        $"from GitHub, and this machine has already used its full unauthenticated GitHub API quota " +
        $"({quota.Used}/{quota.Limit}), which resets at {quota.ResetsAt.ToLocalTime():HH:mm} local time. " +
        "Shared networks reach that cap without any help from OpenClaw." +
        Environment.NewLine + Environment.NewLine + SelfInstallInstructions;

    /// <summary>
    /// Reads the caller's GitHub API quota. Returns null when the quota cannot be
    /// determined; a diagnostic must never turn a recoverable failure into a hard one.
    /// </summary>
    public static async Task<GitHubApiQuota?> QueryGitHubQuotaAsync(CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = s_probeTimeout };
            // GitHub rejects requests without a User-Agent.
            http.DefaultRequestHeaders.UserAgent.ParseAdd("OpenClawSetup");

            using var response = await http.GetAsync(RateLimitUrl, ct);
            if (!response.IsSuccessStatusCode)
                return null;

            await using var body = await response.Content.ReadAsStreamAsync(ct);
            using var json = await JsonDocument.ParseAsync(body, cancellationToken: ct);

            if (!json.RootElement.TryGetProperty("resources", out var resources) ||
                !resources.TryGetProperty("core", out var core))
            {
                return null;
            }

            if (!core.TryGetProperty("limit", out var limit) ||
                !core.TryGetProperty("remaining", out var remaining) ||
                !core.TryGetProperty("reset", out var reset))
            {
                return null;
            }

            return new GitHubApiQuota(
                limit.GetInt32(),
                remaining.GetInt32(),
                DateTimeOffset.FromUnixTimeSeconds(reset.GetInt64()));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }
}
