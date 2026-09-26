namespace OpenClaw.SetupEngine;

/// <summary>The three stages setup progress shows users, in pipeline order.</summary>
public enum SetupStage
{
    CheckPc,
    Install,
    Finish,
}

public enum SetupStageState
{
    Pending,
    Active,
    Done,
    Failed,
}

/// <summary>
/// Condenses pipeline step progress into <see cref="SetupStage"/>s. Every setup step belongs to
/// one stage and stages follow pipeline order. A stage is done once each of its steps finished
/// (succeeded or skipped), a failed step fails its stage, and the last stage is only done once
/// the whole run succeeded, so the condensed view never hides a failure or reports success early.
/// </summary>
public sealed class SetupStageTracker
{
    private static readonly Dictionary<string, SetupStage> StageByStepId = new(StringComparer.Ordinal)
    {
        ["validate-distro-path"] = SetupStage.CheckPc,
        ["preflight-os"] = SetupStage.CheckPc,
        ["validate-local-ai-recovery-gateway"] = SetupStage.CheckPc,
        ["preserve-local-ai-recovery-gateway"] = SetupStage.CheckPc,
        ["preflight-local-ai-hardware"] = SetupStage.CheckPc,
        ["preflight-wsl"] = SetupStage.CheckPc,
        ["preflight-windows-tailscale"] = SetupStage.CheckPc,

        // Can install WSL itself, with an administrator prompt and a download.
        ["ensure-wsl-platform"] = SetupStage.Install,
        ["reconcile-local-ai-installation"] = SetupStage.Install,
        ["acquire-local-ai-runtime"] = SetupStage.Install,
        ["acquire-local-ai-model"] = SetupStage.Install,
        ["persist-local-ai-manifest"] = SetupStage.Install,
        ["start-local-ai-runtime"] = SetupStage.Install,
        ["capture-local-ai-gpu-baseline"] = SetupStage.Install,
        ["verify-local-ai-inference"] = SetupStage.Install,
        ["verify-local-ai-gpu-load"] = SetupStage.Install,
        ["revalidate-local-ai-recovery-gateway"] = SetupStage.Install,
        ["configure-local-ai-wsl-networking"] = SetupStage.Install,
        ["cleanup-distro"] = SetupStage.Install,
        ["cleanup-gateway"] = SetupStage.Install,
        ["preflight-port"] = SetupStage.Install,
        ["wsl-create"] = SetupStage.Install,
        ["wsl-configure"] = SetupStage.Install,
        ["validate-wsl-lockdown"] = SetupStage.Install,
        ["install-cli"] = SetupStage.Install,
        ["verify-local-ai-wsl"] = SetupStage.Install,
        ["install-tailscale"] = SetupStage.Install,
        ["authorize-tailscale"] = SetupStage.Install,
        ["configure-gateway"] = SetupStage.Install,
        ["configure-local-ai-gateway"] = SetupStage.Install,
        ["install-service"] = SetupStage.Install,
        ["start-gateway"] = SetupStage.Install,
        ["restart-gateway"] = SetupStage.Install,
        ["finalize-tailscale-serve"] = SetupStage.Install,

        ["mint-token"] = SetupStage.Finish,
        ["pair-operator"] = SetupStage.Finish,
        ["pair-node"] = SetupStage.Finish,
        ["verify-e2e"] = SetupStage.Finish,
        ["run-wizard"] = SetupStage.Finish,
        ["windows-node-context"] = SetupStage.Finish,
        ["start-keepalive"] = SetupStage.Finish,
    };

    private readonly Dictionary<SetupStage, HashSet<string>> _steps = new();
    private readonly HashSet<string> _finished = new(StringComparer.Ordinal);
    private readonly HashSet<SetupStage> _started = new();
    private readonly HashSet<SetupStage> _failed = new();
    private readonly SetupStage _lastStage;
    private bool _succeeded;

    /// <param name="stepIds">The steps this run will execute.</param>
    public SetupStageTracker(IEnumerable<string> stepIds)
    {
        foreach (var stage in Enum.GetValues<SetupStage>())
            _steps[stage] = new HashSet<string>(StringComparer.Ordinal);
        foreach (var stepId in stepIds)
        {
            if (StageOf(stepId) is { } stage)
                _steps[stage].Add(stepId);
        }

        _lastStage = Enum.GetValues<SetupStage>().LastOrDefault(Includes, SetupStage.Finish);
    }

    public static SetupStage? StageOf(string stepId) =>
        StageByStepId.TryGetValue(stepId, out var stage) ? stage : null;

    /// <summary>Whether this run has any step in <paramref name="stage"/>.</summary>
    public bool Includes(SetupStage stage) => _steps[stage].Count > 0;

    public void StepStarted(string stepId)
    {
        if (TryGetStage(stepId, out var stage))
            _started.Add(stage);
    }

    public void StepFinished(string stepId, StepOutcome outcome)
    {
        if (!TryGetStage(stepId, out var stage))
            return;

        _started.Add(stage);
        if (outcome is StepOutcome.Failed or StepOutcome.FailedTerminal)
            _failed.Add(stage);
        else
            _finished.Add(stepId);
    }

    /// <summary>Marks the whole run as successful, which completes the last stage.</summary>
    public void RunSucceeded() => _succeeded = _failed.Count == 0;

    public SetupStageState StateOf(SetupStage stage)
    {
        if (_failed.Contains(stage))
            return SetupStageState.Failed;
        if (_succeeded || (stage != _lastStage && _started.Contains(stage) && _steps[stage].IsSubsetOf(_finished)))
            return SetupStageState.Done;
        return _started.Contains(stage) ? SetupStageState.Active : SetupStageState.Pending;
    }

    private bool TryGetStage(string stepId, out SetupStage stage)
    {
        stage = default;
        if (StageOf(stepId) is not { } mapped || !_steps[mapped].Contains(stepId))
            return false;

        stage = mapped;
        return true;
    }
}
