using EmbodySense.Core.Common.Credentials.Leases.Models;
using EmbodySense.Core.Common.Secrets.Redaction.Models;

namespace EmbodySense.Core.Common.Credentials.Leases;

/// <summary>Correlates one value-free credential-use record to its exact durable lease and bounded redaction posture.</summary>
public sealed record CredentialLeaseUseEvidence(
    int SchemaVersion,
    CredentialLeaseAttemptHistory Attempt,
    RedactionSummary RedactionSummary)
{
    /// <summary>Gets the only supported experimental schema version.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Gets the exact immutable lease intent.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public CredentialLeaseIntent Intent => Attempt.Intent;

    /// <summary>Gets the exact terminal version hash.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string TerminalVersionHash => Attempt.Current.ContentHash;

    /// <summary>Gets the closed terminal phase.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public CredentialLeasePhase TerminalPhase => Attempt.Current.Phase;

    /// <summary>Gets the durable redemption-boundary time when one was crossed.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public DateTimeOffset? RedemptionBoundaryAtUtc => Attempt.Versions
        .SingleOrDefault(version => version.Phase == CredentialLeasePhase.RedemptionBoundaryReached)
        ?.RecordedAtUtc;

    /// <summary>Gets the terminal trusted time.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public DateTimeOffset TerminalAtUtc => Attempt.Current.RecordedAtUtc;

    /// <summary>Gets the exact current-authority evidence hash retained at authorization.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? CurrentAuthorityEvidenceHash => Attempt.Current.CurrentAuthorityEvidenceHash;

    /// <summary>Gets the exact registry evidence hash retained at authorization.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? RegistryEvidenceHash => Attempt.Current.RegistryEvidenceHash;
}
