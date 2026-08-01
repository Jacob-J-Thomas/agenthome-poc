namespace EmbodySense.Core.Persistence.Credentials.Models;

internal sealed record CredentialRegistryTombstoneDocument(string ReferenceId, long Revision, string OperationId, DateTimeOffset TombstonedAtUtc, string ReferenceHash);
