using EmbodySense.Core.Common.Loops.Models.Custom;

namespace EmbodySense.Core.Application.Loops.Authoring;

public sealed record CustomLoopDefinitionInput(
    string DisplayName,
    string Description,
    CustomLoopTriggerPolicy TriggerPolicy,
    CustomLoopInferenceStepInput[] InferenceSteps,
    CustomLoopToolAssignment[] ToolAssignments,
    CustomLoopExitPolicy ExitPolicy);
