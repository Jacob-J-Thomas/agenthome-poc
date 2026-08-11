using EmbodySense.Core.Application.Loops.Revisions.Models;

namespace EmbodySense.Core.Application.Loops.GraphAuthoring.Models;

/// <summary>Returns one atomic graph aggregate and workspace-global full-intent observation.</summary>
public sealed record GovernedLoopGraphRevisionMutationReadResult(
    GovernedLoopRevisionStoreReadStatus Status,
    long StoreGeneration,
    GovernedLoopGraphRevisionSnapshot? Snapshot,
    GovernedLoopGraphRevisionStoredOperation? ExistingOperation);
