namespace EmbodySense.Core.Application.Governance.Authority.Grants.Models;

/// <summary>Requests current server-owned authorization for one exact canonical grant mutation.</summary>
/// <param name="Request">The complete bounded mutation intent.</param>
/// <param name="RequestHash">The canonical exact-intent hash.</param>
/// <param name="EvaluatedAtUtc">The trusted UTC authorization instant.</param>
public sealed record AuthorityGrantActorAuthorizationRequest(
    AuthorityGrantMutationRequest Request,
    string RequestHash,
    DateTimeOffset EvaluatedAtUtc);
