using EmbodySense.Core.Common.Credentials.Leases.Models;

namespace EmbodySense.Core.Application.Credentials.Leases.Models;

/// <summary>Returns one complete current value-free intent reconstructed from canonical authority sources.</summary>
public sealed record CredentialLeaseCurrentAuthoritySnapshot(
    CredentialLeaseCurrentVerificationStatus Status,
    CredentialLeaseIntent? Intent = null,
    string? EvidenceHash = null);
