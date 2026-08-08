namespace EmbodySense.Core.Common.Authority.Models;

/// <summary>
/// Identifies the bounded profile revision represented in a boundary receipt.
/// </summary>
/// <param name="ProfileId">The stable profile identifier.</param>
/// <param name="Revision">The exact profile revision.</param>
public sealed record AuthorityProfileReference(AuthorityProfileId ProfileId, AuthorityProfileRevision Revision);
