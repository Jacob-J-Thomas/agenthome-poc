using EmbodySense.Core.Common.Authority;

namespace EmbodySense.Core.Application.HumanInput.Lifecycle.Models;

/// <summary>Returns an exact echoed server-owned Human Input lifecycle actor-authorization disposition.</summary>
/// <param name="Status">The closed authorization outcome.</param>
/// <param name="OperationId">The exact echoed operation identity.</param>
/// <param name="RequestHash">The exact echoed canonical command hash.</param>
/// <param name="WorkspaceId">The exact echoed server-owned workspace identity.</param>
/// <param name="EvaluatedAtUtc">The exact echoed trusted UTC instant.</param>
/// <param name="ActorId">The authenticated actor when safely established.</param>
/// <param name="AuthorityEvidenceHash">The bounded server-owned evidence digest.</param>
public sealed partial record HumanInputRequestLifecycleActorAuthorization(
    HumanInputRequestLifecycleActorAuthorizationStatus Status,
    string OperationId,
    string RequestHash,
    string WorkspaceId,
    DateTimeOffset EvaluatedAtUtc,
    AuthorityActorId? ActorId,
    string AuthorityEvidenceHash);
