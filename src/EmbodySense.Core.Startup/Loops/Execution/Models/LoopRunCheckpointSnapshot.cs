namespace EmbodySense.Core.Startup.Loops.Execution.Models;

public sealed record LoopRunCheckpointSnapshot(
    int Iteration,
    int NextStepIndex,
    int AcceptedRepeatCount,
    bool PendingExitDecision,
    IReadOnlyList<LoopRunRetainedOutputSnapshot> EarlierRetainedOutputs,
    LoopRunRetainedOutputSnapshot? PreviousIterationResult,
    LoopRunRetainedOutputSnapshot? CurrentIterationResult,
    int ToolRequestsUsed,
    long LastCommittedSequence);
