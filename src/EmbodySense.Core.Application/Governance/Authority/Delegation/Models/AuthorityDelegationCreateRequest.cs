using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Delegation.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Loops.Admission.Models;

namespace EmbodySense.Core.Application.Governance.Authority.Delegation.Models;

/// <summary>Requests one exact, bounded delegated-authority envelope from immutable parent admission evidence.</summary>
/// <param name="ParentAdmission">The complete hash-valid parent admission receipt.</param>
/// <param name="OriginNodeId">The exact issuing node identity.</param>
/// <param name="OriginNodeAttempt">The exact positive issuing-node attempt.</param>
/// <param name="EnvelopeId">The stable idempotency and envelope identity; reuse must preserve the exact request.</param>
/// <param name="Target">The exact requested role, loop, or node target.</param>
/// <param name="DelegatedCeiling">The requested authority ceiling.</param>
/// <param name="DelegatedCapabilityPins">The exact capability pins describing that ceiling.</param>
/// <param name="TargetClass">The exact non-wildcard target class.</param>
/// <param name="OperationClass">The exact non-wildcard operation class.</param>
/// <param name="Purpose">The exact bounded purpose restriction.</param>
/// <param name="Boundary">The local trusted-time and completion boundary.</param>
public sealed record AuthorityDelegationCreateRequest(
    GovernedLoopAdmissionReceipt ParentAdmission,
    string OriginNodeId,
    int OriginNodeAttempt,
    string EnvelopeId,
    AuthorityDelegationTargetBinding Target,
    AuthorityCeiling DelegatedCeiling,
    IReadOnlyList<CapabilityAdmissionPin> DelegatedCapabilityPins,
    string TargetClass,
    string OperationClass,
    AuthorityPurpose Purpose,
    AuthorityDelegationBoundary Boundary);
