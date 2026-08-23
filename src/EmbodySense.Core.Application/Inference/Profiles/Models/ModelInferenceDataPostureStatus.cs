namespace EmbodySense.Core.Application.Inference.Profiles.Models;

/// <summary>Identifies current exact attempt input-classification posture.</summary>
public enum ModelInferenceDataPostureStatus
{
    /// <summary>Classification evidence is current and complete.</summary>
    Available = 1,
    /// <summary>Classification evidence could not be proved.</summary>
    Unavailable = 2
}
