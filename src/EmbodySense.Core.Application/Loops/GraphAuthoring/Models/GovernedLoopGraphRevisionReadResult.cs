using EmbodySense.Core.Application.Loops.Revisions.Models;

namespace EmbodySense.Core.Application.Loops.GraphAuthoring.Models;

/// <summary>Returns one exact graph aggregate read at a proved store generation.</summary>
public sealed record GovernedLoopGraphRevisionReadResult(
    GovernedLoopRevisionStoreReadStatus Status,
    long StoreGeneration,
    GovernedLoopGraphRevisionSnapshot? Snapshot);
