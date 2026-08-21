namespace EmbodySense.Core.Common.Credentials.Leases.Models;

/// <summary>Defines the value-free outcome retained by each credential-lease version.</summary>
public enum CredentialLeaseOutcome
{
    /// <summary>The attempt is not terminal.</summary>
    Pending = 1,

    /// <summary>No redemption boundary was crossed.</summary>
    NotRedeemed = 2,

    /// <summary>One trusted callback completed.</summary>
    Redeemed = 3,

    /// <summary>The provider conclusively failed without invoking the callback.</summary>
    Failed = 4,

    /// <summary>The boundary was crossed and the callback posture is uncertain.</summary>
    Ambiguous = 5,
}
