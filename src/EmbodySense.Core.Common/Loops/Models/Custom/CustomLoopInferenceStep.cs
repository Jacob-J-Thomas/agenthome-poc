using EmbodySense.Core.Common.Loops.Custom;
namespace EmbodySense.Core.Common.Loops.Models.Custom;

public sealed record CustomLoopInferenceStep(
    string Id,
    string Name,
    string Instruction,
    CustomLoopNodeContextPolicy ContextPolicy);
