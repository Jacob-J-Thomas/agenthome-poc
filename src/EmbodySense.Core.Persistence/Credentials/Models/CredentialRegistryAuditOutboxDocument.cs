namespace EmbodySense.Core.Persistence.Credentials.Models;

internal sealed record CredentialRegistryAuditOutboxDocument(DateTimeOffset OccurredAtUtc, long RegistryRevision, string Action, string Outcome, string Detail);
