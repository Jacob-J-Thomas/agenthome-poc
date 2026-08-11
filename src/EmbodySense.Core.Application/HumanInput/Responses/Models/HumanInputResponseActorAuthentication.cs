using EmbodySense.Core.Common.Authority;

namespace EmbodySense.Core.Application.HumanInput.Responses.Models;

/// <summary>Returns an exact echoed server-owned caller-authentication disposition.</summary>
/// <param name="Status">The closed authentication outcome.</param>
/// <param name="OperationId">The exact echoed operation identity.</param>
/// <param name="CommandHash">The exact echoed command hash.</param>
/// <param name="WorkspaceId">The exact echoed server-owned workspace identity.</param>
/// <param name="EvaluatedAtUtc">The exact echoed trusted UTC instant.</param>
/// <param name="ActorId">The authenticated actor when safely established.</param>
/// <param name="AuthenticationEvidenceHash">The bounded server-owned authentication evidence digest.</param>
public sealed partial record HumanInputResponseActorAuthentication(
    HumanInputResponseActorAuthenticationStatus Status,
    string OperationId,
    string CommandHash,
    string WorkspaceId,
    DateTimeOffset EvaluatedAtUtc,
    AuthorityActorId? ActorId,
    string AuthenticationEvidenceHash);
