namespace EmbodySense.Core.Application.Loops.Revisions.Models;

/// <summary>Returns exact durable evidence and current graph state for one atomic lifecycle commit attempt.</summary>
/// <param name="Status">The atomic persistence disposition.</param>
/// <param name="StoreGeneration">The durable global generation when known, or zero otherwise.</param>
/// <param name="Operation">The durable workspace-global operation binding when known.</param>
/// <param name="Snapshot">The current graph snapshot when safely available.</param>
public sealed record GovernedLoopRevisionStoreCommitResult(
    GovernedLoopRevisionStoreCommitStatus Status,
    long StoreGeneration,
    GovernedLoopRevisionStoredOperation? Operation,
    GovernedLoopRevisionStoreSnapshot? Snapshot);
