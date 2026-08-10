namespace EmbodySense.Core.Application.Loops.Revisions.Models;

internal sealed record PublicationCheck(PublicationCheckStatus Status, string? EvidenceHash)
{
    internal static PublicationCheck NotRequired { get; } = new(PublicationCheckStatus.NotRequired, null);
    internal static PublicationCheck Rejected { get; } = new(PublicationCheckStatus.Rejected, null);
    internal static PublicationCheck Unavailable { get; } = new(PublicationCheckStatus.Unavailable, null);
}
