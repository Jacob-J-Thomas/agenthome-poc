namespace EmbodySense.Core.Persistence.Credentials.Models;

/// <summary>Defines bounded registry retention limits that may only reduce the schema-1 production ceilings.</summary>
public sealed record CredentialRegistryQuota(int MaximumEntries, int MaximumTombstones, int MaximumOperations, int MaximumEvidence, int MaximumArtifactUtf8Bytes);
