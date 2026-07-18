namespace EmbodySense.Core.Common.Loops.Models.Custom;

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
    public const int CurrentSchemaVersion = 1;

    public const string DefaultInferenceInstruction = "Use the invocation input to complete the user's requested task within this loop's governed authority.";

    public const string DefaultExitDecisionInstruction = "Request another iteration only when the latest result still has a concrete, recoverable gap. Otherwise complete.";

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
