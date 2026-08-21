using EmbodySense.Core.Application.Credentials.Leases.Models;
using EmbodySense.Core.Common.Credentials.Leases;
using EmbodySense.Core.Common.Credentials.Leases.Models;

namespace EmbodySense.Core.Application.Credentials.Leases;

/// <summary>Linearly orders restrictive lifecycle mutation and durable lease-boundary publication per credential reference.</summary>
public interface ICredentialLeaseRedemptionGate
{
    /// <summary>Revalidates exact registry state and commits the single-use boundary under one shared reference-scoped gate.</summary>
    Task<CredentialLeaseBoundaryResult> TryEnterAsync(CredentialLeaseAttemptHistory authorized, ICredentialLeaseAttemptLease lease, DateTimeOffset trustedNowUtc, CancellationToken cancellationToken = default);
}
