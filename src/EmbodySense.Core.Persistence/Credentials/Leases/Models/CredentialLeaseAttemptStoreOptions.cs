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

    /// <summary>Gets an optional awaitable observer queued when bounded owner-marker takeover polling begins.</summary>
    /// <remarks>
    /// The observer is queued independently at most once per recovery operation after its first failed exact owner-marker attempt.
    /// The recovery path never awaits it; a detached guard awaits the returned <see cref="ValueTask"/> and ignores all callback or
    /// scheduling exceptions. It receives no cancellation signal, may run after the operation returns, and callers must make captured
    /// state thread-safe and release any callback-owned resources independently. The default production path leaves this unset.
    /// </remarks>
    public Func<ValueTask>? OwnerTakeoverPollingObserver { get; init; }
}
