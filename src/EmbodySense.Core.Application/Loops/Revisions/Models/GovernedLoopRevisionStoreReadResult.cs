namespace EmbodySense.Core.Application.Loops.Revisions.Models;

/// <summary>Returns one atomic graph and workspace-global operation observation used to plan a lifecycle mutation.</summary>
/// <param name="Status">Whether the read is trustworthy.</param>
/// <param name="StoreGeneration">The exact global generation, or zero when it could not be proved.</param>
/// <param name="Snapshot">The graph aggregate when it exists and is trustworthy.</param>
/// <param name="ExistingOperation">The workspace-global operation binding when the operation identifier was already consumed.</param>
public sealed record GovernedLoopRevisionStoreReadResult(
    GovernedLoopRevisionStoreReadStatus Status,
    long StoreGeneration,
    GovernedLoopRevisionStoreSnapshot? Snapshot,
    GovernedLoopRevisionStoredOperation? ExistingOperation);
