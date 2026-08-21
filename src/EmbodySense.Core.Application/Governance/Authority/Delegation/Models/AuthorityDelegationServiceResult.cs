using EmbodySense.Core.Common.Authority.Delegation;
using EmbodySense.Core.Common.Authority.Delegation.Models;

namespace EmbodySense.Core.Application.Governance.Authority.Delegation.Models;

/// <summary>Returns a bounded delegation posture and an envelope only for successful creation or use.</summary>
/// <param name="Status">The closed fail-closed posture.</param>
/// <param name="Envelope">The hash-valid envelope only for <c>Created</c>, <c>Replayed</c>, or <c>Valid</c>.</param>
/// <param name="ReasonCode">The bounded value-free reason code.</param>
public sealed record AuthorityDelegationServiceResult(
    AuthorityDelegationServiceStatus Status,
    AuthorityDelegationEnvelope? Envelope,
    string ReasonCode);
