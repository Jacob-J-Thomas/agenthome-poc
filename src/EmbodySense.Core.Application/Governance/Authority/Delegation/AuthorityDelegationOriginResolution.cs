using EmbodySense.Core.Application.Governance.Authority.Delegation.Models;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Delegation.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Loops.Execution;

namespace EmbodySense.Core.Application.Governance.Authority.Delegation;

/// <summary>Reports exact, server-owned issuer and immutable parent-admission authority evidence.</summary>
public sealed record AuthorityDelegationOriginResolution
{
    /// <summary>Creates a resolution with defensive bounded authority and capability snapshots.</summary>
    public AuthorityDelegationOriginResolution(
        AuthorityDelegationOriginResolutionStatus status,
        string workspaceId,
        GovernedLoopExecutionBinding parentExecution,
        string originNodeId,
        int originNodeAttempt,
        AuthorityDelegationTargetBinding target,
        string targetClass,
        string operationClass,
        AuthorityPurpose purpose,
        AuthorityDelegationCompletionConstraintKind completionConstraint,
        AuthorityCeiling declaredAuthorityMaximum,
        AuthorityCeiling parentEffectiveAuthority,
        IReadOnlyList<CapabilityAdmissionPin> parentCapabilityPins,
        string evidenceHash)
    {
        Status = status;
        WorkspaceId = workspaceId;
        ParentExecution = parentExecution;
        OriginNodeId = originNodeId;
        OriginNodeAttempt = originNodeAttempt;
        Target = target;
        TargetClass = targetClass;
        OperationClass = operationClass;
        Purpose = purpose;
        CompletionConstraint = completionConstraint;
        DeclaredAuthorityMaximum = AuthorityDelegationApplicationCopy.Copy(declaredAuthorityMaximum);
        ParentEffectiveAuthority = AuthorityDelegationApplicationCopy.Copy(parentEffectiveAuthority);
        ParentCapabilityPins = AuthorityDelegationApplicationCopy.CopyPins(parentCapabilityPins);
        EvidenceHash = evidenceHash;
    }

    /// <summary>Gets the exact origin posture.</summary>
    public AuthorityDelegationOriginResolutionStatus Status { get; }

    /// <summary>Gets the canonical workspace.</summary>
    public string WorkspaceId { get; }

    /// <summary>Gets the exact parent execution.</summary>
    public GovernedLoopExecutionBinding ParentExecution { get; }

    /// <summary>Gets the exact origin-node identity.</summary>
    public string OriginNodeId { get; }

    /// <summary>Gets the exact positive origin-node attempt.</summary>
    public int OriginNodeAttempt { get; }

    /// <summary>Gets the exact authored target.</summary>
    public AuthorityDelegationTargetBinding Target { get; }

    /// <summary>Gets the exact authored target class.</summary>
    public string TargetClass { get; }

    /// <summary>Gets the exact authored operation class.</summary>
    public string OperationClass { get; }

    /// <summary>Gets the exact authored purpose.</summary>
    public AuthorityPurpose Purpose { get; }

    /// <summary>Gets the exact authored completion constraint.</summary>
    public AuthorityDelegationCompletionConstraintKind CompletionConstraint { get; }

    /// <summary>Gets the authored maximum that this origin may delegate.</summary>
    public AuthorityCeiling DeclaredAuthorityMaximum { get; }

    /// <summary>Gets the immutable parent-admission effective authority for proof recomputation.</summary>
    public AuthorityCeiling ParentEffectiveAuthority { get; }

    /// <summary>Gets the immutable parent-admission pins for proof recomputation.</summary>
    public IReadOnlyList<CapabilityAdmissionPin> ParentCapabilityPins { get; }

    /// <summary>Gets the stable server-owned semantic origin evidence hash.</summary>
    public string EvidenceHash { get; }
}
