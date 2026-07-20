using EmbodySense.Core.Startup.Loops;

namespace EmbodySense.Core.Startup.Loops.Execution;

public sealed record LoopRunSnapshot(
    int SchemaVersion,
    string Id,
    string LoopId,
    int LifecycleVersion,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string Surface,
    LoopRunModelSnapshot Model,
    string AdmissionOperationId,
    string AdmissionActor,
    string AdmissionRequestHash,
    LoopDefinitionSnapshot AdmittedDefinition,
    string TriggerPrompt,
    LoopRunConversationReference? InvokingConversation,
    LoopRunContextSnapshot Context,
    LoopRunExecutionClockSnapshot ExecutionClock,
    LoopRunCheckpointSnapshot Checkpoint,
    IReadOnlyList<LoopRunEventSnapshot> Events,
    string? FinalOutput,
    string? FailureCode,
    string? FailureDetail);
