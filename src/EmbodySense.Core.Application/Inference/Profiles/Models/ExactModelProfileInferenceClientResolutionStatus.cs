namespace EmbodySense.Core.Application.Inference.Profiles.Models;

/// <summary>Identifies safe exact-client resolution posture.</summary>
public enum ExactModelProfileInferenceClientResolutionStatus
{
    /// <summary>A fresh exact client lease was resolved.</summary>
    Resolved = 1,
    /// <summary>The exact admitted profile/configuration is no longer registered.</summary>
    Ineligible = 2,
    /// <summary>Private configuration or client construction is unavailable.</summary>
    Unavailable = 3
}
