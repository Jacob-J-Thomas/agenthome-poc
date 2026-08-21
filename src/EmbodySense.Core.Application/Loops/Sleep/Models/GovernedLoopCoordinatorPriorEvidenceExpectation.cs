namespace EmbodySense.Core.Application.Loops.Sleep.Models;

/// <summary>States the exact durable evidence expected before an atomic coordinator acquisition.</summary>
public enum GovernedLoopCoordinatorPriorEvidenceExpectation
{
    /// <summary>No prior coordinator evidence may exist.</summary>
    NotFound = 1,

    /// <summary>Exact prior ownership and heartbeat hashes must exist before a lease-expired handoff.</summary>
    Existing = 2
}
