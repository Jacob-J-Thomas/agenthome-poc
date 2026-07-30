namespace EmbodySense.Core.Startup.Loops.Models;

/// <summary>
/// Defines one ordered custom-loop inference node.
/// </summary>
/// <param name="Id">The stable step identity, or null for server assignment during creation.</param>
/// <param name="Name">The user-visible step name.</param>
/// <param name="Instruction">The model instruction executed at this step.</param>
/// <param name="ContextPolicy">The inherited or explicit context policy for this step.</param>
public sealed record LoopInferenceStep(
    string? Id,
    string Name,
    string Instruction,
    LoopNodeContextPolicy ContextPolicy);
