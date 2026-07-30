using EmbodySense.Core.Common.Loops.Custom;
namespace EmbodySense.Core.Common.Loops.Models.Custom;

/// <summary>
/// Represents a custom loop exit policy.
/// </summary>
/// <param name="MaxAdditionalIterations">The maximum additional iterations.</param>
/// <param name="DecisionInstruction">The decision instruction.</param>
/// <param name="ContextPolicy">The context policy.</param>
public sealed record CustomLoopExitPolicy(
    int MaxAdditionalIterations,
    string DecisionInstruction,
    CustomLoopNodeContextPolicy ContextPolicy);
