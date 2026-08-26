namespace EmbodySense.Core.Application.Loops.Sleep.Models;

/// <summary>States the exact durable evidence expected before an atomic coordinator acquisition.</summary>
public enum GovernedLoopCoordinatorPriorEvidenceExpectation
{
    /// <summary>No prior coordinator evidence may exist.</summary>
    NotFound = 1,

    /// <summary>Exact prior ownership and heartbeat hashes must exist before a lease-expired handoff.</summary>
    Existing = 2,

    /// <summary>
    /// The exact current owner may immediately acquire a successor only after its own durable stopped lifecycle reached a
    /// safe boundary. This is distinct from a lease-expired handoff and never authorizes another owner before expiry.
    /// </summary>
    TerminalSameOwner = 3
}
