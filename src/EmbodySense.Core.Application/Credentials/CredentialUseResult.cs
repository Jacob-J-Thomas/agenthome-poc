using EmbodySense.Core.Common.Credentials;
using EmbodySense.Core.Common.Credentials.Leases;
using EmbodySense.Core.Common.Credentials.Leases.Models;
using EmbodySense.Core.Common.Credentials.Models;

namespace EmbodySense.Core.Application.Credentials;

/// <summary>Reports value-free evidence or a structured failure from brokered credential use.</summary>
public sealed class CredentialUseResult
{
    private CredentialUseResult(CredentialUseEvidence? evidence, CredentialFailure? failure, CredentialLeaseAttemptHistory? leaseAttempt)
    {
        Evidence = evidence;
        Failure = failure;
        LeaseAttempt = leaseAttempt;
    }

    /// <summary>Gets value-free evidence when use succeeded.</summary>
    public CredentialUseEvidence? Evidence { get; }

    /// <summary>Gets the closed value-free failure.</summary>
    public CredentialFailure? Failure { get; }

    /// <summary>Gets the exact value-free durable lease posture when broker processing began.</summary>
    public CredentialLeaseAttemptHistory? LeaseAttempt { get; }

    /// <summary>Gets whether exactly one evidence record and no failure were returned.</summary>
    public bool Succeeded => Evidence is not null && Failure is null;

    /// <summary>Creates a successful value-free use result.</summary>
    public static CredentialUseResult Success(CredentialUseEvidence evidence, CredentialLeaseAttemptHistory? leaseAttempt = null) => new(evidence ?? throw new ArgumentNullException(nameof(evidence)), null, leaseAttempt);

    /// <summary>Creates a failed value-free use result.</summary>
    public static CredentialUseResult Failed(CredentialFailure failure, CredentialLeaseAttemptHistory? leaseAttempt = null) => new(null, failure ?? throw new ArgumentNullException(nameof(failure)), leaseAttempt);
}
