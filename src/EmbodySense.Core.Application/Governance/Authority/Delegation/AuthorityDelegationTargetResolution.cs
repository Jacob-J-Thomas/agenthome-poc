using EmbodySense.Core.Application.Governance.Authority.Delegation.Models;
using EmbodySense.Core.Common.Authority.Delegation.Models;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.Loops.Custom;

namespace EmbodySense.Core.Application.Governance.Authority.Delegation;

/// <summary>Reports exact target posture, capability maxima, and value-free semantic evidence.</summary>
public sealed record AuthorityDelegationTargetResolution
{
    /// <summary>Creates a resolution with bounded defensive capability-id snapshots.</summary>
    public AuthorityDelegationTargetResolution(
        AuthorityDelegationTargetResolutionStatus status,
        AuthorityDelegationTargetBinding target,
        string workspaceId,
        IReadOnlyList<string> roleCapabilityIds,
        IReadOnlyList<string> loopCapabilityIds,
        IReadOnlyList<string> nodeCapabilityIds,
        string targetMaximumEvidenceHash)
    {
        Status = status;
        Target = target;
        WorkspaceId = workspaceId;
        RoleCapabilityIds = AuthorityDelegationApplicationCopy.Snapshot(roleCapabilityIds, ContextualRoleLimits.MaxCapabilityMaximums);
        LoopCapabilityIds = AuthorityDelegationApplicationCopy.Snapshot(loopCapabilityIds, CustomLoopLimits.MaxGraphAuthorityCapabilities);
        NodeCapabilityIds = AuthorityDelegationApplicationCopy.Snapshot(nodeCapabilityIds, CustomLoopLimits.MaxGraphAuthorityCapabilities);
        TargetMaximumEvidenceHash = targetMaximumEvidenceHash;
    }

    /// <summary>Gets the exact target posture.</summary>
    public AuthorityDelegationTargetResolutionStatus Status { get; }

    /// <summary>Gets the exact resolved target and stable binding evidence.</summary>
    public AuthorityDelegationTargetBinding Target { get; }

    /// <summary>Gets the canonical target workspace.</summary>
    public string WorkspaceId { get; }

    /// <summary>Gets the exact target-role capability maximum.</summary>
    public IReadOnlyList<string> RoleCapabilityIds { get; }

    /// <summary>Gets the exact target-loop capability maximum.</summary>
    public IReadOnlyList<string> LoopCapabilityIds { get; }

    /// <summary>Gets the exact target-node capability maximum.</summary>
    public IReadOnlyList<string> NodeCapabilityIds { get; }

    /// <summary>Gets the canonical server-resolved target-maximum evidence hash.</summary>
    public string TargetMaximumEvidenceHash { get; }
}
