namespace EmbodySense.Core.Application.LocalWorkspace.Actions.Models;

/// <summary>Describes whether exact canonical effect intent owns a private workspace action artifact.</summary>
public enum WorkspaceActionAttemptPresence
{
    /// <summary>Presence could not be proved safely; cleanup must preserve the artifact.</summary>
    Unknown = 0,

    /// <summary>Exact canonical intent exists and owns the artifact.</summary>
    Exists = 1,

    /// <summary>No canonical attempt exists for the exact idempotency identity and generation.</summary>
    NotFound = 2,

    /// <summary>Exact canonical intent conclusively no longer requires its private preparation artifact.</summary>
    ArtifactReleased = 3,
}
