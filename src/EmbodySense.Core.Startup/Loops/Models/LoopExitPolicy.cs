namespace EmbodySense.Core.Startup.Loops.Models;

/// <summary>
/// Defines the model-gated continuation ceiling and context policy after ordered inference.
/// </summary>
/// <param name="MaxAdditionalIterations">The maximum additional complete iterations after the first.</param>
/// <param name="DecisionInstruction">The instruction used to decide whether another iteration is needed.</param>
/// <param name="ContextPolicy">The inherited or explicit context used for the exit decision and its output.</param>
public sealed record LoopExitPolicy(
    int MaxAdditionalIterations,
    string DecisionInstruction,
    LoopNodeContextPolicy ContextPolicy);
