namespace EmbodySense.Core.Application.Loops.Models;

/// <summary>
/// Retains the append-only evidence of one explicit human review resolution.
/// </summary>
public sealed record DefaultConversationTurnReviewResolution(
    string ResolutionId,
    DefaultConversationTurnReviewDisposition Disposition,
    DateTimeOffset ResolvedAtUtc,
    string Detail);
