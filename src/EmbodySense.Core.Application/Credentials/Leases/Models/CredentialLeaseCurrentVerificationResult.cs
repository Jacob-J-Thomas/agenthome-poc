namespace EmbodySense.Core.Application.Credentials.Leases.Models;

/// <summary>Returns exact value-free hashes from fresh server-owned authority, capability, and profile truth.</summary>
public sealed record CredentialLeaseCurrentVerificationResult(
    CredentialLeaseCurrentVerificationStatus Status,
    string? CurrentAuthorityDecisionHash = null,
    string? CapabilityDescriptorHash = null,
    string? ProfileHash = null,
    string? VerifiedIntentHash = null,
    string? EvidenceHash = null);
