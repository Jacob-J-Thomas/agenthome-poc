using EmbodySense.Core.Common.Loops.Custom;
namespace EmbodySense.Core.Common.Loops.Models.Custom;

/// <summary>
/// Represents a custom loop inference step.
/// </summary>
/// <param name="Id">The stable artifact identifier.</param>
/// <param name="Name">The name.</param>
/// <param name="Instruction">The instruction.</param>
/// <param name="ContextPolicy">The context policy.</param>
public sealed record CustomLoopInferenceStep(
    string Id,
    string Name,
    string Instruction,
    CustomLoopNodeContextPolicy ContextPolicy);
