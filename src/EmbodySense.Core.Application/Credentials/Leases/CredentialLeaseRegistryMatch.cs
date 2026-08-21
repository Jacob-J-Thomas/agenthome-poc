using EmbodySense.Core.Common.Credentials;
using EmbodySense.Core.Common.Credentials.Models;

namespace EmbodySense.Core.Application.Credentials.Leases;

/// <summary>Returns exact value-free registry agreement or one closed failure.</summary>
public sealed record CredentialLeaseRegistryMatch(bool Succeeded, string? EvidenceHash, CredentialFailure? Failure)
{
    /// <summary>Creates an exact accepted registry match.</summary>
    public static CredentialLeaseRegistryMatch Accepted(string evidenceHash) => new(true, evidenceHash, null);

    /// <summary>Creates one closed registry rejection.</summary>
    public static CredentialLeaseRegistryMatch Rejected(CredentialFailureCode code) => new(false, null, CredentialFailure.FromCode(code));
}
