using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
namespace EmbodySense.Core.Common.Loops.Custom.Execution;

/// <summary>
/// Captures the resumable execution cursor and retained reasoning state of a custom-loop run.
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
    /// <summary>
    /// Creates the initial checkpoint before the first inference step.
    /// </summary>
    /// <returns>Iteration 1, step index 0, no accepted repeats, outputs, tool requests, or committed events.</returns>
    public static CustomLoopRunCheckpoint Start()
    {
        return new CustomLoopRunCheckpoint(1, 0, 0, false, [], null, null, 0, 0);
    }
}
