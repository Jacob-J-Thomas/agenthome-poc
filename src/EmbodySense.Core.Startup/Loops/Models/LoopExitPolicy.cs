namespace EmbodySense.Core.Startup.Loops.Models;

public sealed record LoopExitPolicy(
    int MaxAdditionalIterations,
    string DecisionInstruction,
    LoopNodeContextPolicy ContextPolicy);
