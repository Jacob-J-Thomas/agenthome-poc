namespace EmbodySense.Core.Persistence.Credentials.Models;

// The version-1 field name is retained for signed-shape stability; it now identifies any audited lifecycle operation, including an intent.
internal sealed record CredentialRegistryAuditDeliveryDocument(string TerminalOperationId, DateTimeOffset DeliveredAtUtc);
