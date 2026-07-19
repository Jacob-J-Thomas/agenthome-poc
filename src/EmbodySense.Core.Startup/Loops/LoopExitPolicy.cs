namespace EmbodySense.Core.Startup.Loops;

public sealed record LoopExitPolicy(
    int MaxAdditionalIterations,
    string DecisionInstruction,
    LoopNodeContextPolicy ContextPolicy);
