namespace EmbodySense.Core.Common.Credentials.Leases.Models;

/// <summary>Defines the closed durable phases of one nonrenewable credential redemption.</summary>
public enum CredentialLeasePhase
{
    /// <summary>The exact value-free intent is durable and no provider call can have occurred.</summary>
    IntentPrepared = 1,

    /// <summary>Current authority and registry evidence were revalidated without crossing the redemption boundary.</summary>
    Authorized = 2,

    /// <summary>The durable single-use boundary was crossed before provider use.</summary>
    RedemptionBoundaryReached = 3,

    /// <summary>The attempt ended before the redemption boundary.</summary>
    NotRedeemed = 4,

    /// <summary>The provider completed its one trusted callback.</summary>
    Redeemed = 5,

    /// <summary>The provider proved that it did not invoke the trusted callback.</summary>
    RedemptionFailed = 6,

    /// <summary>Material might have been observed and automatic replay is forbidden.</summary>
    RedemptionAmbiguous = 7,
}
