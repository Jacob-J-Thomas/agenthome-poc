using EmbodySense.Core.Common.Capabilities;

namespace EmbodySense.Core.Common.Loops.Admission.Models;

/// <summary>Identifies one exact required root dependency that the effective authority cannot satisfy.</summary>
/// <param name="DependencyId">The canonical required capability identity.</param>
/// <param name="CompatibleVersionRange">The exact required compatible-version range.</param>
/// <param name="Reason">The closed denial reason.</param>
public sealed record GovernedLoopAdmissionCapabilityDenialViolation(
    CapabilityId DependencyId,
    CapabilityVersionRange CompatibleVersionRange,
    GovernedLoopAdmissionCapabilityDenialReason Reason);
