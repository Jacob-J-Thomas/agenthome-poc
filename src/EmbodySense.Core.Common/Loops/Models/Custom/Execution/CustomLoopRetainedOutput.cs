namespace EmbodySense.Core.Common.Loops.Models.Custom.Execution;

public sealed record CustomLoopRetainedOutput(
    string StepId,
    int Iteration,
    string Content,
    string ContentHash);
