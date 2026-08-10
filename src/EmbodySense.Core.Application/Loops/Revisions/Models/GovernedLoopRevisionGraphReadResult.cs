namespace EmbodySense.Core.Application.Loops.Revisions.Models;

/// <summary>Returns one exact graph aggregate read without operation-id replay lookup.</summary>
/// <param name="Status">Whether the read is trustworthy.</param>
/// <param name="StoreGeneration">The exact global store generation, or zero when it could not be proved.</param>
/// <param name="Snapshot">The exact graph snapshot when it exists.</param>
public sealed record GovernedLoopRevisionGraphReadResult(
    GovernedLoopRevisionStoreReadStatus Status,
    long StoreGeneration,
    GovernedLoopRevisionStoreSnapshot? Snapshot);
