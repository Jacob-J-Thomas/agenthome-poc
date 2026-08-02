namespace EmbodySense.Core.Persistence.Credentials.Models;

internal sealed record CredentialRegistryTombstoneDocument(string ReferenceId, long Revision, string OperationId, DateTimeOffset TombstonedAtUtc, string ReferenceHash, bool NeedsRepair = false, string? RepairBindingJson = null, string? RepairProviderId = null);
