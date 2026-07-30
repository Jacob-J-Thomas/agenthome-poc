namespace EmbodySense.Core.Startup.Loops.Execution.Models;

/// <summary>
/// Identifies the exact durable execution position from which a parked run may resume.
/// </summary>
/// <param name="Iteration">The iteration.</param>
/// <param name="NextStepIndex">The next step index.</param>
/// <param name="AcceptedRepeatCount">The accepted repeat count.</param>
/// <param name="PendingExitDecision">The pending exit decision.</param>
/// <param name="EarlierRetainedOutputs">The earlier retained outputs.</param>
/// <param name="PreviousIterationResult">The previous iteration result.</param>
/// <param name="CurrentIterationResult">The current iteration result.</param>
/// <param name="ToolRequestsUsed">The tool requests used.</param>
/// <param name="LastCommittedSequence">The last committed sequence.</param>
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
