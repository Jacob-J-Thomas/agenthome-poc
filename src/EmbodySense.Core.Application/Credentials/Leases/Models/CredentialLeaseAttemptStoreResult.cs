using EmbodySense.Core.Common.Credentials.Leases;
using EmbodySense.Core.Common.Credentials.Leases.Models;

namespace EmbodySense.Core.Application.Credentials.Leases.Models;

/// <summary>Returns one closed durable lease-store posture, exact history, and optional execution ownership.</summary>
public sealed record CredentialLeaseAttemptStoreResult(
    CredentialLeaseAttemptStoreStatus Status,
    CredentialLeaseAttemptHistory? History = null,
    ICredentialLeaseAttemptLease? Lease = null);
