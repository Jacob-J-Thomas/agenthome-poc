using System.Collections.ObjectModel;

namespace EmbodySense.Core.Common.Credentials.Models;

/// <summary>Contains bounded, non-secret public metadata for one credential reference.</summary>
public sealed record CredentialReference(
    int SchemaVersion,
    CredentialReferenceId Id,
    string Type,
    CredentialLifecycleStatus Status,
    string OwnerId,
    string Purpose,
    CredentialProviderId ProviderId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? ExpiresAtUtc,
    IReadOnlyDictionary<string, string> Metadata)
{
    /// <summary>Gets the only supported experimental schema version.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Gets a sorted defensive snapshot of allowlisted non-secret metadata.</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; } = Metadata is null ? null! : new ReadOnlyDictionary<string, string>(Metadata.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
}
