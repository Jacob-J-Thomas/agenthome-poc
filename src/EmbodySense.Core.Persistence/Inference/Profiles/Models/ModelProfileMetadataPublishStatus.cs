namespace EmbodySense.Core.Persistence.Inference.Profiles.Models;

/// <summary>Identifies one server-owned model-profile metadata publication outcome.</summary>
public enum ModelProfileMetadataPublishStatus
{
    /// <summary>A new authenticated metadata revision was published.</summary>
    Published = 1,
    /// <summary>The exact operation and content, or the exact current content, was already retained.</summary>
    AlreadyPresent = 2,
    /// <summary>The operation, expected revision, profile identity, or configuration revision conflicts with retained state.</summary>
    Conflict = 3,
    /// <summary>Trusted durable source state could not be established.</summary>
    Unavailable = 4
}
