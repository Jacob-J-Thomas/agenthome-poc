namespace EmbodySense.Core.Persistence.Authority.Models;

internal sealed record AuthorityGrantRevisionDocument(
    int Revision,
    string GrantJson,
    string ContentHash,
    string OperationId,
    DateTimeOffset RecordedAtUtc);
