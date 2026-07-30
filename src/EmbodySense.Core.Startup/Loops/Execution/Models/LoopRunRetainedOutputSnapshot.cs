namespace EmbodySense.Core.Startup.Loops.Execution.Models;

/// <summary>
/// Projects one canonical model output retained for later loop reasoning or publication.
/// </summary>
/// <param name="StepId">The step identifier.</param>
/// <param name="Iteration">The iteration.</param>
/// <param name="Content">The content.</param>
/// <param name="ContentHash">The content hash.</param>
public sealed record LoopRunRetainedOutputSnapshot(
    string StepId,
    int Iteration,
    string Content,
    string ContentHash);
