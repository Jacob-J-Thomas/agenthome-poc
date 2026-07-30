using EmbodySense.Core.Common.Loops.Models.Custom;

namespace EmbodySense.Core.Application.Loops.Authoring.Models;

/// <summary>
/// Represents a custom loop definition input.
/// </summary>
/// <param name="DisplayName">The human-readable display name.</param>
/// <param name="Description">The human-readable description.</param>
/// <param name="TriggerPolicy">The trigger policy.</param>
/// <param name="InferenceSteps">The inference steps.</param>
/// <param name="ToolAssignments">The tool assignments.</param>
/// <param name="ExitPolicy">The exit policy.</param>
public sealed record CustomLoopDefinitionInput(
    string DisplayName,
    string Description,
    CustomLoopTriggerPolicy TriggerPolicy,
    CustomLoopInferenceStepInput[] InferenceSteps,
    CustomLoopToolAssignment[] ToolAssignments,
    CustomLoopExitPolicy ExitPolicy);
