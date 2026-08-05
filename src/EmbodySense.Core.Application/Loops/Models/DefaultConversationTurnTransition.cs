namespace EmbodySense.Core.Application.Loops.Models;

/// <summary>
/// Records one append-only durable checkpoint transition.
/// </summary>
/// <param name="Sequence">The one-based transition sequence.</param>
/// <param name="TransitionId">The stable transition identity.</param>
/// <param name="Checkpoint">The checkpoint proved by the transition.</param>
/// <param name="OccurredAtUtc">The transition time.</param>
/// <param name="Detail">The non-empty evidence summary.</param>
public sealed record DefaultConversationTurnTransition(
    int Sequence,
    string TransitionId,
    DefaultConversationTurnCheckpoint Checkpoint,
    DateTimeOffset OccurredAtUtc,
    string Detail);
