using EmbodySense.Core.Common.Credentials.Leases.Models;

namespace EmbodySense.Core.Common.Credentials.Leases;

/// <summary>Retains one exact intent and its bounded immutable credential-redemption phase chain.</summary>
public sealed record CredentialLeaseAttemptHistory(
    int SchemaVersion,
    CredentialLeaseIntent Intent,
    IReadOnlyList<CredentialLeaseAttemptVersion> Versions)
{
    /// <summary>Gets the only supported experimental schema version.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Gets an immutable snapshot of the retained versions.</summary>
    public IReadOnlyList<CredentialLeaseAttemptVersion> Versions { get; } = Array.AsReadOnly((Versions ?? []).ToArray());

    /// <summary>Gets the current immutable version.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public CredentialLeaseAttemptVersion Current => Versions[^1];
}
