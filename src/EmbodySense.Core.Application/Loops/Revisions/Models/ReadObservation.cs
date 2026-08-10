namespace EmbodySense.Core.Application.Loops.Revisions.Models;

internal sealed record ReadObservation(
    ReadStatus Status,
    long StoreGeneration,
    GovernedLoopRevisionStoreSnapshot? Snapshot,
    GovernedLoopRevisionStoredOperation? ExistingOperation)
{
    internal static ReadObservation Unavailable { get; } = new(ReadStatus.Unavailable, 0, null, null);
    internal static ReadObservation Ambiguous { get; } = new(ReadStatus.Ambiguous, 0, null, null);
}
