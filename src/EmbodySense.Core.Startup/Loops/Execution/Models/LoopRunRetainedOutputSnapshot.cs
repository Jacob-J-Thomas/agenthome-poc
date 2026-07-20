namespace EmbodySense.Core.Startup.Loops.Execution;

public sealed record LoopRunRetainedOutputSnapshot(
    string StepId,
    int Iteration,
    string Content,
    string ContentHash);
