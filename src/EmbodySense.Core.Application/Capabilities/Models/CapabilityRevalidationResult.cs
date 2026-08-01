using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Returns the currently effective admitted pins after catalog and narrower-authority revalidation.</summary>
public sealed record CapabilityRevalidationResult(bool IsValid, IReadOnlyList<CapabilityAdmissionPin> EffectivePins, string Detail);
