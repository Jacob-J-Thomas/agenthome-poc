namespace EmbodySense.Core.Persistence.Loops.Execution.Authority.Models;

/// <summary>Identifies durable authority-evidence boundaries exposed for recovery evaluation.</summary>
public enum GovernedLoopEffectAuthorityPersistenceBoundary
{
    /// <summary>The last proved ledger was durably published before the candidate primary.</summary>
    ProofPublished = 1,

    /// <summary>The authenticated direct-successor primary was durably published.</summary>
    PrimaryPublished = 2,

    /// <summary>The server-owned monotonic trust anchor advanced to the published primary.</summary>
    TrustAdvanced = 3
}
