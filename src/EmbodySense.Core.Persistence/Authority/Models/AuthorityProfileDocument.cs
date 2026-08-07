namespace EmbodySense.Core.Persistence.Authority.Models;

internal sealed record AuthorityProfileDocument(string ProfileId, IReadOnlyList<AuthorityProfileRevisionDocument> Revisions, AuthorityProfileTombstoneDocument? Tombstone);
