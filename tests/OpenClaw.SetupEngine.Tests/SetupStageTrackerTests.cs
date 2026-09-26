namespace OpenClaw.SetupEngine.Tests;

/// <summary>
/// Pins how setup progress is condensed into three user-facing stages: every pipeline step
/// belongs to one stage in pipeline order, failures stay visible on the stage that failed, and
/// the last stage never reports done before the whole run succeeds.
/// </summary>
public sealed class SetupStageTrackerTests
{
    public static TheoryData<string> Pipelines => ["default", "local-ai-recovery"];

    [Theory]
    [MemberData(nameof(Pipelines))]
    public void EveryPipelineStep_BelongsToAStage_InPipelineOrder(string pipeline)
    {
        var stages = Steps(pipeline).Select(id => (Id: id, Stage: SetupStageTracker.StageOf(id))).ToList();

        Assert.All(stages, step => Assert.True(step.Stage.HasValue, $"{step.Id} has no setup stage."));
        Assert.Equal(stages.Select(step => step.Stage).Order(), stages.Select(step => step.Stage));
    }

    [Fact]
    public void DefaultPipeline_ChecksThePc_ThenInstalls_ThenFinishes()
    {
        Assert.Equal(SetupStage.CheckPc, SetupStageTracker.StageOf("preflight-wsl"));
        // Can install WSL itself (an administrator prompt and a download), so it isn't a check.
        Assert.Equal(SetupStage.Install, SetupStageTracker.StageOf("ensure-wsl-platform"));
        Assert.Equal(SetupStage.Install, SetupStageTracker.StageOf("acquire-local-ai-model"));
        Assert.Equal(SetupStage.Install, SetupStageTracker.StageOf("wsl-create"));
        Assert.Equal(SetupStage.Install, SetupStageTracker.StageOf("start-gateway"));
        Assert.Equal(SetupStage.Finish, SetupStageTracker.StageOf("pair-node"));
        Assert.Equal(SetupStage.Finish, SetupStageTracker.StageOf("start-keepalive"));
        Assert.Null(SetupStageTracker.StageOf("not-a-setup-step"));
    }

    [Fact]
    public void BeforeAnyStep_EveryStageIsPending()
    {
        var tracker = new SetupStageTracker(Steps("default"));

        Assert.All(Enum.GetValues<SetupStage>(), stage => Assert.Equal(SetupStageState.Pending, tracker.StateOf(stage)));
    }

    [Fact]
    public void StartingAStep_MakesItsStageActive_AndLeavesLaterStagesPending()
    {
        var tracker = new SetupStageTracker(Steps("default"));

        tracker.StepStarted("preflight-os");

        Assert.Equal(SetupStageState.Active, tracker.StateOf(SetupStage.CheckPc));
        Assert.Equal(SetupStageState.Pending, tracker.StateOf(SetupStage.Install));
        Assert.Equal(SetupStageState.Pending, tracker.StateOf(SetupStage.Finish));
    }

    [Fact]
    public void AStage_IsDoneOnlyWhenEveryOneOfItsStepsFinished_IncludingSkippedSteps()
    {
        var steps = Steps("default");
        var tracker = new SetupStageTracker(steps);
        var checkPc = steps.Where(id => SetupStageTracker.StageOf(id) == SetupStage.CheckPc).ToList();

        foreach (var id in checkPc.SkipLast(1))
            Run(tracker, id, StepOutcome.Success);
        Assert.Equal(SetupStageState.Active, tracker.StateOf(SetupStage.CheckPc));

        Run(tracker, checkPc[^1], StepOutcome.Skipped);
        Assert.Equal(SetupStageState.Done, tracker.StateOf(SetupStage.CheckPc));
        Assert.Equal(SetupStageState.Pending, tracker.StateOf(SetupStage.Install));
    }

    [Fact]
    public void StepsThePipelineSkips_WithoutAStartEvent_StillFinishTheirStage()
    {
        // The pipeline reports only a Skipped outcome, with no start event, for steps it skips.
        var steps = Steps("default");
        var tracker = new SetupStageTracker(steps);

        foreach (var id in steps.Where(id => SetupStageTracker.StageOf(id) == SetupStage.CheckPc))
            tracker.StepFinished(id, StepOutcome.Skipped);

        Assert.Equal(SetupStageState.Done, tracker.StateOf(SetupStage.CheckPc));
    }

    [Fact]
    public void Finish_IsNotDone_UntilTheRunSucceeds()
    {
        var steps = Steps("default");
        var tracker = new SetupStageTracker(steps);

        foreach (var id in steps)
            Run(tracker, id, StepOutcome.Success);

        Assert.Equal(SetupStageState.Done, tracker.StateOf(SetupStage.CheckPc));
        Assert.Equal(SetupStageState.Done, tracker.StateOf(SetupStage.Install));
        Assert.Equal(SetupStageState.Active, tracker.StateOf(SetupStage.Finish));

        tracker.RunSucceeded();

        Assert.Equal(SetupStageState.Done, tracker.StateOf(SetupStage.Finish));
    }

    [Theory]
    [InlineData(StepOutcome.Failed)]
    [InlineData(StepOutcome.FailedTerminal)]
    public void AFailedStep_FailsItsStage_KeepsEarlierStagesDone_AndLaterStagesPending(StepOutcome failure)
    {
        var steps = Steps("default");
        var tracker = new SetupStageTracker(steps);

        foreach (var id in steps.TakeWhile(id => id != "wsl-create"))
            Run(tracker, id, StepOutcome.Success);
        Run(tracker, "wsl-create", failure);
        tracker.RunSucceeded();

        Assert.Equal(SetupStageState.Done, tracker.StateOf(SetupStage.CheckPc));
        Assert.Equal(SetupStageState.Failed, tracker.StateOf(SetupStage.Install));
        Assert.Equal(SetupStageState.Pending, tracker.StateOf(SetupStage.Finish));
    }

    [Theory]
    [InlineData(StepOutcome.Failed)]
    [InlineData(StepOutcome.FailedTerminal)]
    public void AFailedStepInTheLastStage_FailsIt_EvenIfTheRunIsLaterMarkedSuccessful(StepOutcome failure)
    {
        var steps = Steps("default");
        var tracker = new SetupStageTracker(steps);

        foreach (var id in steps.TakeWhile(id => id != "pair-node"))
            Run(tracker, id, StepOutcome.Success);
        Run(tracker, "pair-node", failure);
        tracker.RunSucceeded();

        Assert.Equal(SetupStageState.Done, tracker.StateOf(SetupStage.CheckPc));
        Assert.Equal(SetupStageState.Done, tracker.StateOf(SetupStage.Install));
        Assert.Equal(SetupStageState.Failed, tracker.StateOf(SetupStage.Finish));
    }

    [Fact]
    public void AFreshTracker_ForARetry_StartsOverFromPending()
    {
        var steps = Steps("default");
        var failed = new SetupStageTracker(steps);
        Run(failed, "preflight-os", StepOutcome.FailedTerminal);

        var retry = new SetupStageTracker(steps);

        Assert.Equal(SetupStageState.Failed, failed.StateOf(SetupStage.CheckPc));
        Assert.All(Enum.GetValues<SetupStage>(), stage => Assert.Equal(SetupStageState.Pending, retry.StateOf(stage)));
    }

    [Fact]
    public void LocalAiRecovery_HasNoFinishStage_AndCompletesWhenTheRunSucceeds()
    {
        var steps = Steps("local-ai-recovery");
        var tracker = new SetupStageTracker(steps);

        Assert.True(tracker.Includes(SetupStage.CheckPc));
        Assert.True(tracker.Includes(SetupStage.Install));
        Assert.False(tracker.Includes(SetupStage.Finish));

        foreach (var id in steps)
            Run(tracker, id, StepOutcome.Success);
        // Install is this run's last stage, so it stays active until the whole run succeeds.
        Assert.Equal(SetupStageState.Done, tracker.StateOf(SetupStage.CheckPc));
        Assert.Equal(SetupStageState.Active, tracker.StateOf(SetupStage.Install));

        tracker.RunSucceeded();

        Assert.Equal(SetupStageState.Done, tracker.StateOf(SetupStage.Install));
    }

    [Fact]
    public void StepsOutsideThePipeline_AreIgnored()
    {
        var tracker = new SetupStageTracker(["preflight-os"]);

        Run(tracker, "wsl-create", StepOutcome.FailedTerminal);
        tracker.StepStarted("not-a-setup-step");

        Assert.Equal(SetupStageState.Pending, tracker.StateOf(SetupStage.CheckPc));
        Assert.Equal(SetupStageState.Pending, tracker.StateOf(SetupStage.Install));
    }

    private static void Run(SetupStageTracker tracker, string stepId, StepOutcome outcome)
    {
        tracker.StepStarted(stepId);
        tracker.StepFinished(stepId, outcome);
    }

    private static List<string> Steps(string pipeline) =>
        (pipeline == "default" ? SetupStepFactory.BuildDefaultSteps() : SetupStepFactory.BuildLocalAiRecoverySteps())
            .Select(step => step.Id)
            .ToList();
}
