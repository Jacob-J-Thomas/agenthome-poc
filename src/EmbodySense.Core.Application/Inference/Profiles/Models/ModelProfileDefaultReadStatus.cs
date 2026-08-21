namespace EmbodySense.Core.Application.Inference.Profiles.Models;

/// <summary>Identifies the result of resolving the trusted configured default profile.</summary>
public enum ModelProfileDefaultReadStatus
{
    /// <summary>An exact configured default was found.</summary>
    Found = 1,
    /// <summary>No default is configured.</summary>
    NotConfigured = 2,
    /// <summary>The trusted default source is unavailable.</summary>
    Unavailable = 3
}
