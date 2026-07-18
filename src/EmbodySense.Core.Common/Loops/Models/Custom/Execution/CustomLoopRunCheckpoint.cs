namespace EmbodySense.Core.Common.Loops.Models.Custom.Execution;

public sealed record CustomLoopRunCheckpoint(
    int Iteration,
    int NextStepIndex,
    int AcceptedRepeatCount,
    bool PendingExitDecision,
    CustomLoopRetainedOutput[] EarlierRetainedOutputs,
    CustomLoopRetainedOutput? PreviousIterationResult,
    CustomLoopRetainedOutput? CurrentIterationResult,
    int ToolRequestsUsed,
    long LastCommittedSequence)
{
    public static CustomLoopRunCheckpoint Start()
    {
        return new CustomLoopRunCheckpoint(1, 0, 0, false, [], null, null, 0, 0);
    }
}
