using EmbodySense.Core.Startup.Loops.Execution;

namespace EmbodySense.Core.Startup.Loops;

public enum LoopTriggerPromptSource
{
    Unknown = 0,
    Invocation = 1,
    Preset = 2,
    None = 3
}

public enum LoopContextPolicyMode
{
    Unknown = 0,
    Inherit = 1,
    Custom = 2
}

public enum LoopToolAssignment
{
    Unknown = 0,
    List = 1,
    Read = 2,
    Search = 3,
    Append = 4,
    Write = 5,
    Delete = 6
}

public enum LoopCustomToolAuthorityCeiling
{
    Unknown = 0,
    WorkspaceReadOnly = 1
}

public sealed record LoopTriggerPolicy(
    LoopTriggerPromptSource PromptSource,
    string PresetPrompt,
    bool IncludeInvokingConversation);

public sealed record LoopContextInputPolicy(
    bool IncludeRoleContext,
    bool IncludeTriggerPrompt,
    bool IncludeInvokingConversation,
    bool IncludeEarlierRetainedOutputs,
    bool IncludePreviousIterationResult);

public sealed record LoopContextOutputPolicy(
    bool RetainForLoopReasoning,
    bool PublishToInvokingConversation);

public sealed record LoopContextPolicy(
    LoopContextInputPolicy ContextIn,
    LoopContextOutputPolicy ContextOut);

public sealed record LoopNodeContextPolicy(
    LoopContextPolicyMode Mode,
    LoopContextPolicy? CustomPolicy);

public sealed record LoopContextDefaults(
    LoopContextPolicy Inference,
    LoopContextPolicy Exit);

public sealed record LoopInferenceStep(
    string? Id,
    string Name,
    string Instruction,
    LoopNodeContextPolicy ContextPolicy);

public sealed record LoopExitPolicy(
    int MaxAdditionalIterations,
    string DecisionInstruction,
    LoopNodeContextPolicy ContextPolicy);

public sealed record LoopDefinitionInput(
    string DisplayName,
    string Description,
    LoopTriggerPolicy TriggerPolicy,
    IReadOnlyList<LoopInferenceStep> InferenceSteps,
    IReadOnlyList<LoopToolAssignment> ToolAssignments,
    LoopExitPolicy ExitPolicy);

public sealed record LoopDefinitionSnapshot(
    int SchemaVersion,
    string Id,
    int DefinitionVersion,
    string ContentHash,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string DisplayName,
    string Description,
    string RoleId,
    LoopTriggerPolicy TriggerPolicy,
    LoopContextDefaults ContextDefaults,
    IReadOnlyList<LoopInferenceStep> InferenceSteps,
    IReadOnlyList<LoopToolAssignment> ToolAssignments,
    LoopExitPolicy ExitPolicy,
    string LastMutationOperationId);

public sealed record LoopValidationError(string Code, string Field, string Message);

public sealed record LoopDefinitionConflict(
    string LoopId,
    int ExpectedDefinitionVersion,
    int ActualDefinitionVersion,
    string CurrentContentHash,
    DateTimeOffset CurrentUpdatedAtUtc);

public sealed record LoopAuthoringResponse(
    string Status,
    bool IsCommitted,
    LoopDefinitionSnapshot? Definition,
    IReadOnlyList<LoopValidationError> ValidationErrors,
    LoopDefinitionConflict? Conflict,
    string? Detail);

public sealed record LoopAuthoringCatalog(
    string RoleId,
    LoopDefinitionSnapshot SystemDefault,
    IReadOnlyList<LoopDefinitionSnapshot> CustomDefinitions,
    LoopAuthoringLimits Limits,
    LoopToolCatalog Tools)
{
    public LoopRunModelSnapshot? RuntimeModel { get; init; }
}

public sealed record LoopToolCatalog(
    IReadOnlyList<LoopToolAssignment> CustomAssignable,
    LoopCustomToolAuthorityCeiling CustomAuthorityCeiling);

public sealed record LoopAuthoringLimits(
    int MaxDefinitionsPerWorkspace,
    int MinInferenceSteps,
    int MaxInferenceSteps,
    int MaxAdditionalIterations,
    int MaxModelAttemptsPerRun,
    int MaxGovernedToolRequestsPerAttempt,
    int MaxGovernedToolRequestsPerRun,
    int MaxNameCharacters,
    int MaxDescriptionCharacters,
    int MaxInstructionCharacters,
    int MaxTriggerPromptCharacters,
    int MaxInvokingConversationCharacters,
    int MaxInvokingConversationEntries,
    int MaxGovernedToolTargetCharacters,
    int MaxGovernedToolArgumentCharacters,
    int MaxToolGovernanceDetailCharacters,
    int MaxCanonicalModelOutputCharacters,
    int MaxCanonicalToolResultCharacters,
    int MaxLifecycleControlEventsPerRun,
    int MaxTraceEventsPerRun,
    int MaxLifecycleControlDetailCharacters,
    int MaxRunTraceUtf8Bytes,
    long MaxRunExecutionMilliseconds);
