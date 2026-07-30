using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom;

namespace EmbodySense.Core.Application.Loops.Authoring.Models;

/// <summary>
/// Represents a custom loop inference step input.
/// </summary>
/// <param name="Id">The stable artifact identifier.</param>
/// <param name="Name">The name.</param>
/// <param name="Instruction">The instruction.</param>
/// <param name="ContextPolicy">The context policy.</param>
public sealed record CustomLoopInferenceStepInput(
    string? Id,
    string Name,
    string Instruction,
    CustomLoopNodeContextPolicy ContextPolicy);
