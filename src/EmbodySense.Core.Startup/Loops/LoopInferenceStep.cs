namespace EmbodySense.Core.Startup.Loops;

public sealed record LoopInferenceStep(
    string? Id,
    string Name,
    string Instruction,
    LoopNodeContextPolicy ContextPolicy);
