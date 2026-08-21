namespace EmbodySense.Core.Application.Loops.Sleep.Models;

/// <summary>Reports conclusive or ambiguous evidence for one exact continuation operation.</summary>
/// <param name="Status">The closed continuation posture.</param>
/// <param name="ContinuationEvidenceHash">The exact committed continuation evidence hash.</param>
/// <param name="EvidenceReference">The bounded value-free reference explaining a non-commit posture.</param>
public sealed record GovernedLoopWakeContinuationResult(
    GovernedLoopWakeContinuationStatus Status,
    string? ContinuationEvidenceHash = null,
    string? EvidenceReference = null);
