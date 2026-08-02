using EmbodySense.Core.Common.Loops.Models;

namespace EmbodySense.Core.Startup.Loops.Models;

/// <summary>
/// Provides an honest read-only projection of a system-owned loop definition.
/// </summary>
/// <remarks>
/// Unlike <see cref="LoopDefinitionSnapshot"/>, this contract does not synthesize custom-loop trigger,
/// inference-step, context-default, tool-assignment, or exit-policy fields. It exposes the system definition's
/// canonical graph and authority policy directly.
/// </remarks>
/// <param name="SchemaVersion">The persisted system-definition schema version.</param>
/// <param name="Id">The stable loop identifier.</param>
/// <param name="DisplayName">The human-readable loop name.</param>
/// <param name="Description">The human-readable loop purpose.</param>
/// <param name="RoleId">The contextual role that owns the loop.</param>
/// <param name="Trigger">The implemented trigger policy.</param>
/// <param name="MemoryScope">The implemented memory and startup-context scope.</param>
/// <param name="CapabilityIds">The loop-scoped capability identifiers.</param>
/// <param name="ReviewPolicy">The loop review policy.</param>
/// <param name="FailurePolicy">The loop failure policy.</param>
/// <param name="State">The current loop state.</param>
/// <param name="EditMode">The system definition edit mode.</param>
/// <param name="Graph">The canonical system-loop graph.</param>
/// <param name="ExecutionContract">The current dedicated-runner contract.</param>
public sealed record SystemLoopDefinitionSnapshot(
    int SchemaVersion,
    string Id,
    string DisplayName,
    string Description,
    string RoleId,
    LoopTrigger Trigger,
    LoopMemoryScope MemoryScope,
    IReadOnlyList<string> CapabilityIds,
    LoopReviewPolicy ReviewPolicy,
    LoopFailurePolicy FailurePolicy,
    LoopState State,
    LoopEditMode EditMode,
    SystemLoopGraphSnapshot Graph,
    SystemLoopExecutionContractSnapshot ExecutionContract);
