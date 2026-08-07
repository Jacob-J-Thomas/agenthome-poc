namespace EmbodySense.Core.Common.Authority.Models;

/// <summary>
/// Records bounded profile-revision provenance without asserting trust, approval, assignment, or authority.
/// </summary>
/// <param name="ActorId">The recorded actor identifier.</param>
/// <param name="Kind">The evidence category.</param>
/// <remarks>Provenance is evidence only and must be evaluated by a separately governed authority source.</remarks>
public sealed record AuthorityProvenance(AuthorityActorId ActorId, AuthorityProvenanceKind Kind);
