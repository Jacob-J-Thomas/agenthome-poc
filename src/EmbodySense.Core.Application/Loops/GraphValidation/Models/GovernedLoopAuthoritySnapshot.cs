using EmbodySense.Core.Application.ContextualRoles.Models;
using EmbodySense.Core.Common.ContextualRoles.Models;

namespace EmbodySense.Core.Application.Loops.GraphValidation.Models;

/// <summary>Captures the current non-widening role authority and resource maxima used by graph admission.</summary>
/// <param name="IsAvailable">Whether current role authority could be resolved authoritatively.</param>
/// <param name="SourceEvidenceId">The stable source evidence identity.</param>
/// <param name="OwningRole">The exact contextual-role revision to which the authority applies.</param>
/// <param name="RoleRevision">The validated immutable role revision.</param>
/// <param name="RoleLifecycle">The re-confirmed active lifecycle evidence.</param>
/// <param name="WorkspaceId">The canonical workspace scope against which applicability was proved.</param>
/// <param name="SourceStatus">The value-free registered instruction-source posture.</param>
/// <param name="CapabilityIds">The current capability maximum.</param>
/// <param name="MaxAttempts">The current graph-wide attempt maximum.</param>
/// <param name="MaxPayloadCharacters">The current graph-wide payload maximum.</param>
/// <param name="MaxEvidenceItems">The current graph-wide evidence maximum.</param>
/// <param name="MaxResourceUnits">The current graph-wide resource-unit maximum.</param>
public sealed record GovernedLoopAuthoritySnapshot(
    bool IsAvailable,
    string SourceEvidenceId,
    ContextualRoleRevisionPin? OwningRole,
    ContextualRoleRevision? RoleRevision,
    ContextualRoleLifecycleSnapshot? RoleLifecycle,
    string WorkspaceId,
    ContextualRoleInstructionSourceProbeStatus SourceStatus,
    IReadOnlyList<string> CapabilityIds,
    int MaxAttempts,
    int MaxPayloadCharacters,
    int MaxEvidenceItems,
    int MaxResourceUnits)
{
    /// <summary>Gets a defensive immutable copy of the exact role capability maximum.</summary>
    public IReadOnlyList<string> CapabilityIds { get; init; } = CapabilityIds is null ? null! : Array.AsReadOnly(CapabilityIds.ToArray());
}
