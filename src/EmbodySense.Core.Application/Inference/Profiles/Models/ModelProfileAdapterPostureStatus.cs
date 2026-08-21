namespace EmbodySense.Core.Application.Inference.Profiles.Models;

/// <summary>Identifies exact adapter registration and current compatibility posture.</summary>
public enum ModelProfileAdapterPostureStatus
{
    /// <summary>The exact pin is registered, compatible, and healthy.</summary>
    Ready = 1,
    /// <summary>No adapter is registered for the exact pin.</summary>
    Unregistered = 2,
    /// <summary>The adapter contract or configuration drifted.</summary>
    Incompatible = 3,
    /// <summary>The adapter is currently degraded.</summary>
    Degraded = 4,
    /// <summary>The adapter is unavailable.</summary>
    Unavailable = 5
}
