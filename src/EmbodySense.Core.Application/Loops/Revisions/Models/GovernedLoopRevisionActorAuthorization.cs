using EmbodySense.Core.Common.Authority;

namespace EmbodySense.Core.Application.Loops.Revisions.Models;

/// <summary>Returns one bounded server-owned authority decision bound to the exact request identity.</summary>
/// <param name="Status">The closed decision.</param>
/// <param name="OperationId">The exact evaluated operation identifier.</param>
/// <param name="RequestHash">The exact evaluated canonical request hash.</param>
/// <param name="ActorId">The exact evaluated actor identity.</param>
/// <param name="AuthorityEvidenceHash">The lowercase SHA-256 digest of bounded current authority evidence.</param>
public sealed record GovernedLoopRevisionActorAuthorization(
    GovernedLoopRevisionActorAuthorizationStatus Status,
    string OperationId,
    string RequestHash,
    AuthorityActorId ActorId,
    string AuthorityEvidenceHash);
