namespace EmbodySense.Core.Persistence.Loops.Revisions.Models;

internal sealed record GovernedLoopRevisionLoadResult(
    GovernedLoopRevisionStoreDocument? Document,
    GovernedLoopRevisionStoreDocument? Pending,
    GovernedLoopRevisionLoadDisposition Disposition);
