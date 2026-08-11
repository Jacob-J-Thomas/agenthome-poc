namespace EmbodySense.Core.Application.Loops.Execution.Authority.Models;

/// <summary>Returns the append-only persistence posture for one exact authority decision.</summary>
/// <param name="Status">The closed store outcome.</param>
/// <param name="ContentHash">The exact persisted decision hash for appended, replayed, or conflicting content when safely known.</param>
public sealed record GovernedLoopEffectAuthorityEvidenceStoreResult(
    GovernedLoopEffectAuthorityEvidenceStoreStatus Status,
    string? ContentHash);
