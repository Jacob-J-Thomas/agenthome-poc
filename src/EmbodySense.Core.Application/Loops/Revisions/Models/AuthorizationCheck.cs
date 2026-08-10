namespace EmbodySense.Core.Application.Loops.Revisions.Models;

internal sealed record AuthorizationCheck(GovernedLoopRevisionActorAuthorizationStatus Status, string EvidenceHash)
{
    internal static AuthorizationCheck Unavailable { get; } = new(GovernedLoopRevisionActorAuthorizationStatus.Unavailable, string.Empty);
}
