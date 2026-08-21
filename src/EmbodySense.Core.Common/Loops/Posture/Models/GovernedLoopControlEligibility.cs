namespace EmbodySense.Core.Common.Loops.Posture.Models;

/// <summary>Describes one currently eligible control using exact optimistic evidence.</summary>
/// <param name="Kind">The closed control kind.</param>
/// <param name="ExpectedRevision">The exact target revision that must still be current.</param>
/// <param name="ExpectedEvidenceHash">The exact lowercase SHA-256 evidence hash that must still be current.</param>
public sealed record GovernedLoopControlEligibility(
    GovernedLoopOperationalControlKind Kind,
    long ExpectedRevision,
    string ExpectedEvidenceHash);
