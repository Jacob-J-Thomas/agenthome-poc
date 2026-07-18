using System.Text.Json.Serialization;

namespace EmbodySense.Core.Common.Loops.Models.Custom.Execution;

public sealed record CustomLoopRunRecord(
    int SchemaVersion,
    string Id,
    string LoopId,
    int LifecycleVersion,
    CustomLoopRunStatus Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string Surface,
    CustomLoopModelSnapshot ModelSnapshot,
    string AdmissionOperationId,
    string AdmissionActor,
    string AdmissionRequestHash,
    CustomLoopDefinition AdmittedDefinition,
    string TriggerPrompt,
    CustomLoopConversationReference? InvokingConversation,
    CustomLoopContextSnapshot ContextSnapshot,
    CustomLoopExecutionClock ExecutionClock,
    CustomLoopRunCheckpoint Checkpoint,
    CustomLoopRunEvent[] Events,
    string? FinalOutput,
    string? FailureCode,
    string? FailureDetail)
{
    public const int CurrentSchemaVersion = 2;

    [JsonIgnore]
    public bool IsTerminal => Status is CustomLoopRunStatus.Completed or CustomLoopRunStatus.Failed or CustomLoopRunStatus.Cancelled or CustomLoopRunStatus.NeedsReview;
}
