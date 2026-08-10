namespace EmbodySense.Core.Persistence.Loops.Revisions.Models;

/// <summary>Identifies durable governed-loop revision-store boundaries exposed for recovery evaluation.</summary>
public enum GovernedLoopRevisionPersistenceBoundary
{
    /// <summary>The last proved document was durably published before the candidate primary.</summary>
    ProofPublished = 1,
    /// <summary>The authenticated direct-successor primary was durably published.</summary>
    PrimaryPublished = 2,
    /// <summary>The server-owned monotonic trust anchor advanced to the published primary.</summary>
    TrustAdvanced = 3
}
