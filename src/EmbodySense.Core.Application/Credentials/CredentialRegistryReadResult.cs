using EmbodySense.Core.Application.Credentials.Models;
using EmbodySense.Core.Common.Credentials;
using EmbodySense.Core.Common.Credentials.Models;

namespace EmbodySense.Core.Application.Credentials;

/// <summary>Returns one safe registry snapshot or a value-free failure.</summary>
public sealed record CredentialRegistryReadResult(long? RegistryRevision, IReadOnlyList<CredentialRegistryEntry> Entries, IReadOnlyList<CredentialRegistryTombstone> Tombstones, IReadOnlyList<CredentialRegistryOperationEvidence> Operations, IReadOnlyList<CredentialUseEvidence> Evidence, CredentialFailure? Failure)
{
    /// <summary>Gets whether the snapshot is trustworthy.</summary>
    public bool Succeeded => Failure is null;
}
