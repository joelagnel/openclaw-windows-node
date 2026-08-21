namespace OpenClaw.SetupEngine.Tests;

/// <summary>
/// wsl --install downloads WSL through the GitHub API, whose unauthenticated
/// quota is 60/hour per IP. Machines behind shared egress hit that cap through
/// unrelated traffic and the install exits 1 with output we cannot capture, so
/// these tests pin the wording that has to stand in for the real error.
/// </summary>
public class WslPlatformInstallDiagnosticsTests
{
    private static GitHubApiQuota Exhausted() =>
        new(60, 0, DateTimeOffset.UtcNow.AddMinutes(14));

    private static GitHubApiQuota Available() =>
        new(60, 41, DateTimeOffset.UtcNow.AddMinutes(14));

    [Fact]
    public void DescribeFailure_ExhaustedQuota_NamesQuotaAndStoreFallback()
    {
        var message = WslPlatformInstallDiagnostics.DescribeFailure(1, Exhausted());

        Assert.Contains("exit code 1", message);
        Assert.Contains("GitHub", message);
        Assert.Contains("60/60", message);
        Assert.Contains(WslInstallSupport.UpdateUrl, message);
    }

    [Fact]
    public void DescribeFailure_QuotaAvailable_StillOffersStoreFallback()
    {
        var message = WslPlatformInstallDiagnostics.DescribeFailure(5, Available());

        Assert.Contains("exit code 5", message);
        Assert.DoesNotContain("resets at", message);
        Assert.Contains(WslInstallSupport.UpdateUrl, message);
    }

    [Fact]
    public void DescribeFailure_UnknownQuota_DoesNotClaimARateLimit()
    {
        var message = WslPlatformInstallDiagnostics.DescribeFailure(1, quota: null);

        Assert.Contains("exit code 1", message);
        Assert.DoesNotContain("quota", message);
        Assert.Contains(WslInstallSupport.UpdateUrl, message);
    }

    [Fact]
    public void DescribeUnavailableDownload_ExplainsWhyNoElevationPromptAppeared()
    {
        var message = WslPlatformInstallDiagnostics.DescribeUnavailableDownload(Exhausted());

        Assert.Contains("60/60", message);
        Assert.Contains("GitHub", message);
        Assert.Contains(WslInstallSupport.UpdateUrl, message);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(-1, true)]
    [InlineData(1, false)]
    [InlineData(60, false)]
    public void IsExhausted_TracksRemainingCalls(int remaining, bool expected) =>
        Assert.Equal(expected, new GitHubApiQuota(60, remaining, DateTimeOffset.UtcNow).IsExhausted);

    [Fact]
    public void Used_NeverReportsNegativeConsumption() =>
        Assert.Equal(0, new GitHubApiQuota(60, 75, DateTimeOffset.UtcNow).Used);

    [Fact]
    public async Task InstallWslPlatform_ExhaustedQuota_FailsWithoutRaisingAnElevationPrompt()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"openclaw-wsl-diag-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var logger = new SetupLogger(filePath: null, LogLevel.Trace);
            var ctx = new SetupContext(
                new SetupConfig(),
                logger,
                new TransactionJournal(filePath: null),
                new CommandRunner(logger),
                CancellationToken.None,
                dataDir: tempDir,
                localDataDir: tempDir);

            var probeCalls = 0;
            StepResult result = await PreflightWslStep.InstallWslPlatformAsync(
                ctx,
                _ =>
                {
                    probeCalls++;
                    return Task.FromResult<GitHubApiQuota?>(Exhausted());
                },
                CancellationToken.None);

            // One probe, no second probe: the installer was never launched, so no
            // administrator prompt was raised for an install that cannot succeed.
            Assert.Equal(1, probeCalls);
            Assert.Equal(StepOutcome.Failed, result.Outcome);
            Assert.Contains("60/60", result.Message);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
