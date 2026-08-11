using EmbodySense.Core.Application.Loops.Revisions.Models;

namespace EmbodySense.Core.Application.Loops.GraphAuthoring.Models;

/// <summary>Returns the exact durable outcome of one compound graph revision commit.</summary>
public sealed record GovernedLoopGraphRevisionCommitResult(
    GovernedLoopRevisionStoreCommitStatus Status,
    long StoreGeneration,
    GovernedLoopGraphRevisionStoredOperation? Operation,
    GovernedLoopGraphRevisionSnapshot? Snapshot);
