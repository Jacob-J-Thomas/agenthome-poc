namespace EmbodySense.Core.Persistence.Authority.Models;

internal sealed record AuthorityProfileRevisionDocument(int Revision, string ProfileJson, string ProfileHash, string OperationId, DateTimeOffset RecordedAtUtc);
