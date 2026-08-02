namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Identifies a durable artifact-store operation outcome.</summary>
public enum CapabilityArtifactStoreStatus
{
    /// <summary>The requested state was durably applied.</summary>
    Applied = 1,
    /// <summary>The immutable artifact was already staged.</summary>
    NoChange = 2,
    /// <summary>The exact operation was replayed.</summary>
    Replayed = 3,
    /// <summary>The optimistic revision was stale.</summary>
    Conflict = 4,
    /// <summary>The requested artifact or prior activation does not exist.</summary>
    NotFound = 5,
    /// <summary>The request conflicts with existing immutable evidence.</summary>
    Invalid = 6,
    /// <summary>The store could not prove a safe durable outcome.</summary>
    Unavailable = 7
}
