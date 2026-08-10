using EmbodySense.Core.Common.Authority;

namespace EmbodySense.Core.Application.Governance.Authority.Grants.Models;

/// <summary>Returns an exact echoed server-owned actor-authorization disposition.</summary>
/// <param name="Status">The closed authorization outcome.</param>
/// <param name="OperationId">The exact echoed operation identity.</param>
/// <param name="RequestHash">The exact echoed canonical request hash.</param>
/// <param name="ActorId">The exact echoed authenticated actor.</param>
/// <param name="EvaluatedAtUtc">The exact echoed trusted UTC authorization instant.</param>
/// <param name="AuthorityEvidenceHash">The bounded server-owned evidence digest.</param>
public sealed record AuthorityGrantActorAuthorization(
    AuthorityGrantActorAuthorizationStatus Status,
    string OperationId,
    string RequestHash,
    AuthorityActorId ActorId,
    DateTimeOffset EvaluatedAtUtc,
    string AuthorityEvidenceHash);
