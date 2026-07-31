using EmbodySense.Core.Startup.Loops.Models;
using EmbodySense.Core.Startup.Loops;

namespace EmbodySense.Core.Startup.Loops.Execution.Models;

/// <summary>
/// Provides the full durable projection of one admitted custom-loop run and its evidence.
/// </summary>
/// <param name="SchemaVersion">The schema version.</param>
/// <param name="Id">The value identifier.</param>
/// <param name="LoopId">The loop identifier.</param>
/// <param name="LifecycleVersion">The lifecycle version.</param>
/// <param name="Status">The status.</param>
/// <param name="CreatedAtUtc">The created at utc.</param>
/// <param name="UpdatedAtUtc">The updated at utc.</param>
/// <param name="CompletedAtUtc">The completed at utc.</param>
/// <param name="Surface">The surface.</param>
/// <param name="Model">The model.</param>
/// <param name="AdmissionOperationId">The admission operation identifier.</param>
/// <param name="AdmissionActor">The admission actor.</param>
/// <param name="AdmissionRequestHash">The admission request hash.</param>
/// <param name="AdmittedDefinition">The admitted definition.</param>
/// <param name="TriggerPrompt">The trigger prompt.</param>
/// <param name="InvokingConversation">The invoking conversation.</param>
/// <param name="Context">The context.</param>
/// <param name="ExecutionClock">The execution clock.</param>
/// <param name="Checkpoint">The checkpoint.</param>
/// <param name="Events">The events.</param>
/// <param name="FinalOutput">The final output.</param>
/// <param name="FailureCode">The failure code.</param>
/// <param name="FailureDetail">The failure detail.</param>
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
