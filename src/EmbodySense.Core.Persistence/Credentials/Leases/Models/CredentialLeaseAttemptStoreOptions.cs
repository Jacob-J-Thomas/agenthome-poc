using EmbodySense.Core.Common.Credentials.Leases;

namespace EmbodySense.Core.Persistence.Credentials.Leases.Models;

/// <summary>Configures finite workspace-local credential lease-attempt retention.</summary>
public sealed record CredentialLeaseAttemptStoreOptions
{
    /// <summary>Gets or initializes the maximum retained credential-use identities.</summary>
    public int MaxAttempts { get; init; } = 4_096;

    /// <summary>Gets or initializes the maximum canonical UTF-8 bytes retained for one history head.</summary>
    public int MaxRecordUtf8Bytes { get; init; } = CredentialLeaseContractLimits.MaximumRecordUtf8Bytes;

    /// <summary>Gets or initializes the maximum UTF-8 bytes retained across the store.</summary>
    public long MaxStoreUtf8Bytes { get; init; } = 128L * 1024 * 1024;

    /// <summary>Gets or initializes the maximum immutable protocol versions retained per identity.</summary>
    public int MaxVersionsPerAttempt { get; init; } = CredentialLeaseContractLimits.MaximumVersions;
}
