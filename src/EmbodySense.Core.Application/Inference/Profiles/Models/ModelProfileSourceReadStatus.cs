namespace EmbodySense.Core.Application.Inference.Profiles.Models;

/// <summary>Identifies the result of reading server-owned model-profile metadata.</summary>
public enum ModelProfileSourceReadStatus
{
    /// <summary>The exact metadata was found.</summary>
    Found = 1,
    /// <summary>No profile metadata exists for the exact capability ID.</summary>
    NotFound = 2,
    /// <summary>Trusted metadata could not be established.</summary>
    Unavailable = 3
}
