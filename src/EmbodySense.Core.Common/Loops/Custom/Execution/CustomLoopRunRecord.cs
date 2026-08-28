using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using System.Text.Json.Serialization;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Loops.Sequential.Models;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Wait;
using EmbodySense.Core.Common.Loops.Execution.Wait.Models;
using EmbodySense.Core.Common.Loops.HumanInput.Checkpoints;

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
    private IReadOnlyList<GovernedLoopWaitExecutionEvidence>? _waitEvidence = Array.AsReadOnly(Array.Empty<GovernedLoopWaitExecutionEvidence>());
    private IReadOnlyList<GovernedLoopHumanInputWaitingCheckpoint>? _humanInputWaitingCheckpoints = Array.AsReadOnly(Array.Empty<GovernedLoopHumanInputWaitingCheckpoint>());

    /// <summary>
    /// Schema version required by the current custom-loop run contract.
    /// </summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Gets the immutable exact capability resolution admitted for this run.</summary>
    public CapabilityAdmissionSnapshot CapabilityAdmission { get; init; } = null!;

    /// <summary>Gets the exact bounded invocation payload copied from the pre-admission operation, or null for the fenced legacy path.</summary>
    [JsonRequired]
    public GovernedLoopSequentialInvocationSnapshot? SequentialInvocationSnapshot { get; init; }

    /// <summary>Gets the exact canonical admission and graph binding, or null for the fenced legacy path.</summary>
    [JsonRequired]
    public GovernedLoopSequentialAdapterBinding? SequentialAdapterBinding { get; init; }

    /// <summary>Gets the exact durable canonical execution frontier, or null only for the explicitly isolated legacy path.</summary>
    /// <remarks>The JSON property is required even when its value is null; schema-1 artifacts that omit it are unsupported.</remarks>
    [JsonRequired]
    public GovernedLoopFrontierPosture? Frontier { get; init; }

    /// <summary>Gets the bounded activation-ordered Wait evidence retained atomically with the canonical frontier and run events.</summary>
    /// <remarks>The JSON property is required even when empty; schema-1 artifacts that omit it are unsupported.</remarks>
    [JsonRequired]
    public IReadOnlyList<GovernedLoopWaitExecutionEvidence> WaitEvidence
    {
        get => _waitEvidence!;
        init => _waitEvidence = value is null
            ? null
            : Array.AsReadOnly(value.Select(GovernedLoopWaitContractCopy.Copy).ToArray());
    }

    /// <summary>Gets the bounded append-only Human Input checkpoints that were atomically published with their exact waiting frontiers.</summary>
    /// <remarks>The JSON property is required even when empty; schema-1 artifacts that omit it are unsupported.</remarks>
    [JsonRequired]
    public IReadOnlyList<GovernedLoopHumanInputWaitingCheckpoint> HumanInputWaitingCheckpoints
    {
        get => _humanInputWaitingCheckpoints!;
        init => _humanInputWaitingCheckpoints = value is null
            ? null
            : Array.AsReadOnly(value.Select(item => GovernedLoopHumanInputWaitingCheckpointContractCopy.Copy(item)).ToArray());
    }

    /// <summary>Gets the required schema-1 Human Review state plane, or null when this run is not controlled by Human Review.</summary>
    /// <remarks>The JSON property is required even when null; omission is an unsupported schema-1 artifact rather than a compatibility path.</remarks>
    [JsonRequired]
    public HumanReviewRunState? HumanReview { get; init; }

    /// <summary>
    /// Gets a value indicating whether the lifecycle has reached a terminal status.
    /// </summary>
    /// <value><see langword="true"/> for completed, failed, cancelled, or needs-review status; otherwise, <see langword="false"/>.</value>
    [JsonIgnore]
    public bool IsTerminal => Status is CustomLoopRunStatus.Completed or CustomLoopRunStatus.Failed or CustomLoopRunStatus.Cancelled or CustomLoopRunStatus.NeedsReview;
}
