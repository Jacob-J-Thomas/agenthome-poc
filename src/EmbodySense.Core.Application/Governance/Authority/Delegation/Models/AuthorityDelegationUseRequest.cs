using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Delegation;
using EmbodySense.Core.Common.Authority.Delegation.Models;
using EmbodySense.Core.Common.Loops.Execution;

namespace EmbodySense.Core.Application.Governance.Authority.Delegation.Models;

/// <summary>Requests current validation of one envelope for one exact intended use.</summary>
/// <param name="Envelope">The complete immutable envelope.</param>
/// <param name="WorkspaceId">The current exact workspace.</param>
/// <param name="ParentExecution">The current exact parent run, revision, and generation.</param>
/// <param name="OriginNodeId">The current exact issuer node.</param>
/// <param name="OriginNodeAttempt">The current exact issuer-node attempt.</param>
/// <param name="Target">The exact intended target.</param>
/// <param name="TargetClass">The exact intended target class.</param>
/// <param name="OperationClass">The exact intended operation class.</param>
/// <param name="Purpose">The exact intended purpose.</param>
public sealed record AuthorityDelegationUseRequest(
    AuthorityDelegationEnvelope Envelope,
    string WorkspaceId,
    GovernedLoopExecutionBinding ParentExecution,
    string OriginNodeId,
    int OriginNodeAttempt,
    AuthorityDelegationTargetBinding Target,
    string TargetClass,
    string OperationClass,
    AuthorityPurpose Purpose);
