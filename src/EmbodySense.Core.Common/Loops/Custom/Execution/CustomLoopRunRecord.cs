using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using System.Text.Json.Serialization;

namespace EmbodySense.Core.Common.Loops.Custom.Execution;

/// <summary>
/// Persists the admission identity, lifecycle, resumable checkpoint, append-only evidence, and terminal outcome of one custom-loop run.
/// </summary>
/// <param name="SchemaVersion">The persisted schema version.</param>
/// <param name="Id">The stable artifact identifier.</param>
/// <param name="LoopId">The owning loop identifier.</param>
/// <param name="LifecycleVersion">The monotonically increasing lifecycle version.</param>
/// <param name="Status">The status.</param>
/// <param name="CreatedAtUtc">The UTC creation time.</param>
/// <param name="UpdatedAtUtc">The UTC last-update time.</param>
/// <param name="CompletedAtUtc">The UTC terminal time, or <see langword="null"/> while nonterminal.</param>
/// <param name="Surface">The normalized owning runtime surface.</param>
/// <param name="ModelSnapshot">The provider and model identity admitted for the run.</param>
/// <param name="AdmissionOperationId">The idempotency identity of the admission operation.</param>
/// <param name="AdmissionActor">The actor identity recorded for admission.</param>
/// <param name="AdmissionRequestHash">The integrity hash of the immutable admission inputs.</param>
/// <param name="AdmittedDefinition">The exact versioned loop definition admitted for execution.</param>
/// <param name="TriggerPrompt">The exact invocation prompt admitted to the run.</param>
/// <param name="InvokingConversation">The optional immutable conversation reference captured at admission.</param>
/// <param name="ContextSnapshot">The immutable provenance-tagged context captured at admission.</param>
/// <param name="ExecutionClock">The accumulated execution-time state.</param>
/// <param name="Checkpoint">The resumable execution cursor and retained reasoning state.</param>
/// <param name="Events">The append-only ordered run evidence.</param>
/// <param name="FinalOutput">The terminal model output, or <see langword="null"/> before completion.</param>
/// <param name="FailureCode">The stable terminal failure code, or <see langword="null"/> for non-failed runs.</param>
/// <param name="FailureDetail">The human-readable terminal failure detail, or <see langword="null"/> when not applicable.</param>
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
    /// <summary>
    /// Schema version required by the current custom-loop run contract.
    /// </summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// Gets a value indicating whether the lifecycle has reached a terminal status.
    /// </summary>
    /// <value><see langword="true"/> for completed, failed, cancelled, or needs-review status; otherwise, <see langword="false"/>.</value>
    [JsonIgnore]
    public bool IsTerminal => Status is CustomLoopRunStatus.Completed or CustomLoopRunStatus.Failed or CustomLoopRunStatus.Cancelled or CustomLoopRunStatus.NeedsReview;
}
