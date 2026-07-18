using EmbodySense.Core.Common.Loops.Models.Custom;

namespace EmbodySense.Core.Application.Loops.Authoring;

public sealed record CustomLoopInferenceStepInput(
    string? Id,
    string Name,
    string Instruction,
    CustomLoopNodeContextPolicy ContextPolicy);
