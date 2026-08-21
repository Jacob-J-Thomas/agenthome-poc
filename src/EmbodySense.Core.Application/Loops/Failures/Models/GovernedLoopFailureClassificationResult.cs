using EmbodySense.Core.Common.Loops.Failures.Models;

namespace EmbodySense.Core.Application.Loops.Failures.Models;

/// <summary>Returns one exact canonical classification or a fail-closed invalid posture.</summary>
/// <param name="Status">The classification posture.</param>
/// <param name="Evidence">The authenticated failure evidence when safely produced.</param>
/// <param name="Detail">Bounded value-free classification detail.</param>
public sealed record GovernedLoopFailureClassificationResult(
    GovernedLoopFailureClassificationStatus Status,
    GovernedLoopFailureEvidence? Evidence,
    string Detail);
