namespace EmbodySense.Core.Persistence.Authority.Models;

internal sealed record AuthorityGrantDocument(string GrantId, IReadOnlyList<AuthorityGrantRevisionDocument> Revisions);
