using EmbodySense.Core.Common.Credentials.Models;

namespace EmbodySense.Core.Common.Credentials.Leases.Models;

/// <summary>Retains one immutable directly hash-linked phase of a credential lease.</summary>
public sealed record CredentialLeaseAttemptVersion(
    int SchemaVersion,
    string LeaseId,
    long Version,
    string IntentHash,
    CredentialLeasePhase Phase,
    CredentialLeaseOutcome Outcome,
    DateTimeOffset RecordedAtUtc,
    string? CurrentAuthorityEvidenceHash,
    string? RegistryEvidenceHash,
    CredentialFailureCode? FailureCode,
    string? PreviousContentHash,
    string ContentHash)
{
    /// <summary>Gets the only supported experimental schema version.</summary>
    public const int CurrentSchemaVersion = 1;
}
