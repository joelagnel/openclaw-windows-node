using OpenClaw.Connection.LocalAi;

namespace OpenClaw.Connection.Tests;

public sealed class LocalAiRuntimeModelsTests
{
    [Fact]
    public void VerifiedModelEvidence_RequiresDigestAndObservedSize()
    {
        var now = DateTimeOffset.Parse("2026-08-18T12:00:00Z");

        Assert.Throws<ArgumentException>(() =>
            new LocalAiModelEvidence(LocalAiModelAvailabilityState.Verified, now));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new LocalAiModelEvidence(
                LocalAiModelAvailabilityState.Verified,
                now,
                new string('a', 64),
                0));
    }

    [Fact]
    public void LoadedModelEvidence_RequiresServerObservedModelIdentifier()
    {
        var now = DateTimeOffset.Parse("2026-08-18T12:00:00Z");

        Assert.Throws<ArgumentException>(() =>
            new LocalAiModelEvidence(
                LocalAiModelAvailabilityState.Loaded,
                now,
                new string('a', 64),
                42));

        var evidence = new LocalAiModelEvidence(
            LocalAiModelAvailabilityState.Loaded,
            now,
            new string('a', 64),
            42,
            "qwen3.6-35b-a3b-q4");

        Assert.Equal(LocalAiModelAvailabilityState.Loaded, evidence.State);
        Assert.Equal("qwen3.6-35b-a3b-q4", evidence.ServerModelId);
    }

    [Fact]
    public void InitialSnapshot_DoesNotClaimManagedOwnershipOrModelAvailability()
    {
        var now = DateTimeOffset.Parse("2026-08-18T12:00:00Z");

        var snapshot = LocalAiRuntimeSnapshot.Initial(new Uri("http://127.0.0.1:18803/v1"), now);

        Assert.Equal(LocalAiOwnership.None, snapshot.Ownership);
        Assert.Equal(LocalAiModelAvailabilityState.Unknown, snapshot.ModelEvidence.State);
        Assert.Null(snapshot.ProcessId);
    }
}
