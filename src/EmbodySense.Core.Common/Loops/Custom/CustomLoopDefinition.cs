using EmbodySense.Core.Common.Loops.Models.Custom;
namespace EmbodySense.Core.Common.Loops.Custom;

/// <summary>
/// Represents a custom loop definition.
/// </summary>
/// <param name="SchemaVersion">The persisted schema version.</param>
/// <param name="Id">The stable artifact identifier.</param>
/// <param name="DefinitionVersion">The monotonically increasing definition version.</param>
/// <param name="ContentHash">The lowercase SHA-256 digest of the exact content.</param>
/// <param name="CreatedAtUtc">The UTC creation time.</param>
/// <param name="UpdatedAtUtc">The UTC last-update time.</param>
/// <param name="DisplayName">The human-readable display name.</param>
/// <param name="Description">The human-readable description.</param>
/// <param name="RoleId">The workspace role identifier.</param>
/// <param name="TriggerPolicy">The trigger policy.</param>
/// <param name="ContextDefaults">The context defaults.</param>
/// <param name="InferenceSteps">The inference steps.</param>
/// <param name="ToolAssignments">The tool assignments.</param>
/// <param name="ExitPolicy">The exit policy.</param>
/// <param name="LastMutationOperationId">The last mutation operation ID.</param>
public sealed record CustomLoopDefinition(
    int SchemaVersion,
    string Id,
    int DefinitionVersion,
    string ContentHash,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string DisplayName,
    string Description,
    string RoleId,
    CustomLoopTriggerPolicy TriggerPolicy,
    CustomLoopContextDefaults ContextDefaults,
    CustomLoopInferenceStep[] InferenceSteps,
    CustomLoopToolAssignment[] ToolAssignments,
    CustomLoopExitPolicy ExitPolicy,
    string LastMutationOperationId)
{
    /// <summary>
    /// Schema version required by the current custom-loop definition contract.
    /// </summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// Default instruction for a newly seeded inference step.
    /// </summary>
    public const string DefaultInferenceInstruction = "Use the invocation input to complete the user's requested task within this loop's governed authority.";

    /// <summary>
    /// Default instruction for deciding whether a seeded loop needs another iteration.
    /// </summary>
    public const string DefaultExitDecisionInstruction = "Request another iteration only when the latest result still has a concrete, recoverable gap. Otherwise complete.";

    /// <summary>
    /// Creates a version-1 custom-loop definition with one inference step, no tool assignments, and the canonical content hash applied.
    /// </summary>
    /// <param name="id">The persisted definition identifier.</param>
    /// <param name="roleId">The workspace role identifier captured by the definition.</param>
    /// <param name="inferenceStepId">The identifier for the initial inference step.</param>
    /// <param name="lastMutationOperationId">The idempotency identity of the creating mutation.</param>
    /// <param name="createdAtUtc">The UTC creation and initial-update timestamp.</param>
    /// <returns>The seeded definition with definition version 1 and a matching canonical content hash.</returns>
    public static CustomLoopDefinition CreateSeed(
        string id,
        string roleId,
        string inferenceStepId,
        string lastMutationOperationId,
        DateTimeOffset createdAtUtc)
    {
        var seed = new CustomLoopDefinition(
            CurrentSchemaVersion,
            id,
            1,
            string.Empty,
            createdAtUtc,
            createdAtUtc,
            "Untitled loop",
            string.Empty,
            roleId,
            new CustomLoopTriggerPolicy(CustomLoopTriggerPromptSource.Invocation, string.Empty, IncludeInvokingConversation: false),
            CustomLoopContextDefaults.CreatePrototypeDefaults(),
            [new CustomLoopInferenceStep(inferenceStepId, "First step", DefaultInferenceInstruction, CustomLoopNodeContextPolicy.Inherit())],
            [],
            new CustomLoopExitPolicy(0, DefaultExitDecisionInstruction, CustomLoopNodeContextPolicy.Inherit()),
            lastMutationOperationId);

        return CustomLoopDefinitionContentHash.Apply(seed);
    }
}
