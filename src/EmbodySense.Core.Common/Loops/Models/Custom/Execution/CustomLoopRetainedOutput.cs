namespace EmbodySense.Core.Common.Loops.Models.Custom.Execution;

/// <summary>
/// Represents a custom loop retained output.
/// </summary>
/// <param name="StepId">The step ID.</param>
/// <param name="Iteration">The iteration.</param>
/// <param name="Content">The exact content.</param>
/// <param name="ContentHash">The lowercase SHA-256 digest of the exact content.</param>
public sealed record CustomLoopRetainedOutput(
    string StepId,
    int Iteration,
    string Content,
    string ContentHash);
