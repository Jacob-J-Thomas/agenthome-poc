namespace EmbodySense.Core.Common.Inference.Profiles.Models;

/// <summary>Identifies one append-only provider-usage ledger phase.</summary>
public enum GovernedModelUsageLedgerPhase
{
    /// <summary>The phase is absent or unsupported.</summary>
    Unknown = 0,
    /// <summary>The maximum usage reservation is durable before transport.</summary>
    ReservationCommitted = 1,
    /// <summary>Authoritative evidence proves provider dispatch did not start.</summary>
    DispatchProvedNotStarted = 2,
    /// <summary>The provider transport boundary was reached.</summary>
    DispatchBoundaryReached = 3,
    /// <summary>Provider usage or explicit unavailable posture was observed.</summary>
    UsageObserved = 4,
    /// <summary>Authoritative usage and proved-unused reservation were reconciled.</summary>
    Reconciled = 5,
    /// <summary>Conflicting, invalid, or over-budget evidence requires attention.</summary>
    AttentionRequired = 6
}
