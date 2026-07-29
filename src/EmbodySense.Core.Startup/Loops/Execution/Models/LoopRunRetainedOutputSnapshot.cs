namespace EmbodySense.Core.Startup.Loops.Execution.Models;

public sealed record LoopRunRetainedOutputSnapshot(
    string StepId,
    int Iteration,
    string Content,
    string ContentHash);
