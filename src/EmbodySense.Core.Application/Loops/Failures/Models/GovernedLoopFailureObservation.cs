using EmbodySense.Core.Common.Loops.Failures.Models;

namespace EmbodySense.Core.Application.Loops.Failures.Models;

/// <summary>Reports one bounded adapter-neutral failure observation without selecting policy or authority.</summary>
/// <param name="Kind">The closed server-owned observation kind.</param>
/// <param name="Source">The exact server-owned subsystem that produced the observation.</param>
/// <param name="ServerCode">The bounded stable server code.</param>
/// <param name="CausalEvidence">The exact source evidence reference.</param>
/// <param name="SafeDetail">Optional already-redacted value-free detail.</param>
public sealed record GovernedLoopFailureObservation(
    GovernedLoopFailureObservationKind Kind,
    GovernedLoopFailureSource Source,
    string ServerCode,
    GovernedLoopFailureEvidenceReference CausalEvidence,
    string? SafeDetail = null);
