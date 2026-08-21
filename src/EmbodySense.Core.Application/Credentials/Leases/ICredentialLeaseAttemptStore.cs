using EmbodySense.Core.Application.Credentials.Leases.Models;
using EmbodySense.Core.Common.Credentials.Leases;
using EmbodySense.Core.Common.Credentials.Leases.Models;

namespace EmbodySense.Core.Application.Credentials.Leases;

/// <summary>Persists immutable, append-only, value-free credential lease attempts.</summary>
public interface ICredentialLeaseAttemptStore
{
    /// <summary>Durably creates or exactly replays a prepared lease intent.</summary>
    Task<CredentialLeaseAttemptStoreResult> BeginAsync(CredentialLeaseIntent intent, CredentialLeaseAttemptVersion prepared, CancellationToken cancellationToken = default);

    /// <summary>Commits one direct hash-linked history successor under exact owner and head.</summary>
    Task<CredentialLeaseAttemptStoreResult> CompareExchangeAsync(string expectedContentHash, CredentialLeaseAttemptHistory replacement, ICredentialLeaseAttemptLease lease, CancellationToken cancellationToken = default);

    /// <summary>Reads and, when unfinished, exclusively resumes one stable credential-use generation for recovery.</summary>
    Task<CredentialLeaseAttemptStoreResult> ResumeAsync(string credentialUseOperationId, long credentialUseGeneration, CancellationToken cancellationToken = default);

    /// <summary>Reads the exact durable posture without taking execution ownership.</summary>
    Task<CredentialLeaseAttemptStoreResult> ReadAsync(string credentialUseOperationId, long credentialUseGeneration, CancellationToken cancellationToken = default);
}
