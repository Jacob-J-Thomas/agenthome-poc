using EmbodySense.Core.Common.Credentials;
using EmbodySense.Core.Common.Credentials.Models;

namespace EmbodySense.Core.Application.Credentials.Models;

/// <summary>Projects immutable, value-free tombstone evidence and whether its append-only cleanup repair is currently unresolved.</summary>
public sealed record CredentialRegistryTombstone(CredentialReferenceId ReferenceId, long Revision, CredentialContractId OperationId, DateTimeOffset TombstonedAtUtc, CredentialContractHash ReferenceHash, bool NeedsRepair = false, CredentialCapabilityBinding? RepairBinding = null, CredentialProviderId? RepairProviderId = null);
