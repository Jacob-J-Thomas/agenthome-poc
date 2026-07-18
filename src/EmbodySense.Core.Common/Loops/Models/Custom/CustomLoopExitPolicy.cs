namespace EmbodySense.Core.Common.Loops.Models.Custom;

public sealed record CustomLoopExitPolicy(
    int MaxAdditionalIterations,
    string DecisionInstruction,
    CustomLoopNodeContextPolicy ContextPolicy);
