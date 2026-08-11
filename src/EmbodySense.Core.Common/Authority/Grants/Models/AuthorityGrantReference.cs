namespace EmbodySense.Core.Common.Authority.Grants.Models;

/// <summary>Identifies one exact immutable grant revision and its canonical content hash.</summary>
/// <param name="GrantId">The stable grant identity.</param>
/// <param name="Revision">The exact immutable revision.</param>
/// <param name="ContentHash">The canonical content hash.</param>
public sealed record AuthorityGrantReference(AuthorityGrantId GrantId, AuthorityGrantRevision Revision, string ContentHash);
