namespace EmbodySense.Core.Startup.Loops.Models;

public sealed record LoopDefinitionInput(
    string DisplayName,
    string Description,
    LoopTriggerPolicy TriggerPolicy,
    IReadOnlyList<LoopInferenceStep> InferenceSteps,
    IReadOnlyList<LoopToolAssignment> ToolAssignments,
    LoopExitPolicy ExitPolicy);
