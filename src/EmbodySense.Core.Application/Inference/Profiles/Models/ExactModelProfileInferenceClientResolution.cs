using EmbodySense.Core.Application.Inference.Profiles;

namespace EmbodySense.Core.Application.Inference.Profiles.Models;

/// <summary>Returns a fresh client lease only for an exact admitted profile/configuration pin.</summary>
public sealed record ExactModelProfileInferenceClientResolution(ExactModelProfileInferenceClientResolutionStatus Status, IExactModelProfileInferenceClientLease? Lease);
