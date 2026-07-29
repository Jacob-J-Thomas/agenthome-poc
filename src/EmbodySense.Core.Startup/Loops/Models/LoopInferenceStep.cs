namespace EmbodySense.Core.Startup.Loops.Models;

public sealed record LoopInferenceStep(
    string? Id,
    string Name,
    string Instruction,
    LoopNodeContextPolicy ContextPolicy);
