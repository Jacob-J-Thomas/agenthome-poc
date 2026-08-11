using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Returns the currently effective admitted pins after catalog and narrower-authority revalidation.</summary>
/// <param name="IsValid">Whether every admitted pin remains exact and effective.</param>
/// <param name="EffectivePins">The bounded exact admitted pins that remain currently valid; this may be partial for a stopped result.</param>
/// <param name="Detail">A bounded operator-safe explanation.</param>
/// <param name="Status">The exact current capability posture.</param>
/// <param name="ObservedPins">Bounded current pins that share an admitted stable id but have drifted from its exact identity.</param>
public sealed record CapabilityRevalidationResult(
    bool IsValid,
    IReadOnlyList<CapabilityAdmissionPin> EffectivePins,
    string Detail,
    CapabilityRevalidationStatus Status = CapabilityRevalidationStatus.Unknown,
    IReadOnlyList<CapabilityAdmissionPin>? ObservedPins = null);
