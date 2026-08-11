namespace EmbodySense.Core.Application.Loops.GraphAuthoring.Models;

internal sealed record GovernedLoopGraphAuthoringDurableProof(
    GovernedLoopGraphAuthoringStatus? StatusOverride,
    GovernedLoopGraphRevisionStoredOperation? Operation,
    GovernedLoopGraphRevisionSnapshot? Snapshot)
{
    internal static GovernedLoopGraphAuthoringDurableProof None { get; } = new(null, null, null);
    internal static GovernedLoopGraphAuthoringDurableProof Ambiguous { get; } = new(GovernedLoopGraphAuthoringStatus.Ambiguous, null, null);
}
