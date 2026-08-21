namespace EmbodySense.Core.Common.Loops.Posture.Models;

/// <summary>Reports one durable, replayed, rejected, or review-required operational control.</summary>
public sealed record GovernedLoopOperationalControlResult(
    GovernedLoopOperationalControlStatus Status,
    string OperationId,
    GovernedLoopOperationalControlKind Kind,
    string TargetId,
    string ReasonCode,
    long? CurrentRevision,
    string? CurrentEvidenceHash,
    string? ReceiptHash,
    int MatchedCount,
    int AppliedCount,
    int NeedsReviewCount);
