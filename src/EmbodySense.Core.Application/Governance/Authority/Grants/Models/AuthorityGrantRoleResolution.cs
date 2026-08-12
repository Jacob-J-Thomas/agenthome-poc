using EmbodySense.Core.Application.ContextualRoles.Models;
using EmbodySense.Core.Common.ContextualRoles.Models;

namespace EmbodySense.Core.Application.Governance.Authority.Grants.Models;

/// <summary>Returns exact current contextual-role revision posture without following replacement.</summary>
/// <param name="Status">The closed exact-dependency posture.</param>
/// <param name="RequestedPin">The exact caller-supplied role pin when valid.</param>
/// <param name="Revision">The immutable exact role revision when safely proved.</param>
/// <param name="Lifecycle">The current stable-role lifecycle projection when safely proved.</param>
/// <param name="WorkspaceId">The canonical workspace scope used to evaluate applicability.</param>
/// <param name="SourceStatus">The value-free registered instruction-source posture.</param>
/// <param name="EvidenceHash">The canonical current-state evidence digest when safely proved.</param>
public sealed record AuthorityGrantRoleResolution(
    AuthorityGrantDependencyStatus Status,
    ContextualRoleRevisionPin? RequestedPin,
    ContextualRoleRevision? Revision,
    ContextualRoleLifecycleSnapshot? Lifecycle,
    string WorkspaceId,
    ContextualRoleInstructionSourceProbeStatus SourceStatus,
    string EvidenceHash);
