namespace EmbodySense.Core.Persistence.Authority.Models;

internal sealed record AuthorityProfileTombstoneDocument(string OperationId, string ActorId, string Reason, DateTimeOffset RecordedAtUtc);
