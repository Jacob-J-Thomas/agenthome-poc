namespace EmbodySense.Core.Startup.Loops.Models;

/// <summary>
/// Provides the client-authored portion of a custom-loop replacement request.
/// </summary>
/// <param name="DisplayName">The user-visible loop name.</param>
/// <param name="Description">The user-visible loop purpose.</param>
/// <param name="TriggerPolicy">How the invocation prompt is resolved and whether conversation context is admitted.</param>
/// <param name="InferenceSteps">One to five ordered inference steps, subject to current limits.</param>
/// <param name="ToolAssignments">Requested tools, later bounded by role authority and the implemented catalog.</param>
/// <param name="ExitPolicy">The continuation ceiling, decision instruction, and exit context policy.</param>
public sealed record LoopDefinitionInput(
    string DisplayName,
    string Description,
    LoopTriggerPolicy TriggerPolicy,
    IReadOnlyList<LoopInferenceStep> InferenceSteps,
    IReadOnlyList<LoopToolAssignment> ToolAssignments,
    LoopExitPolicy ExitPolicy);
