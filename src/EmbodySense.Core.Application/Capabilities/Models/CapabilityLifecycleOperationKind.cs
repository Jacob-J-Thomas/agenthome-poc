namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Identifies one capability lifecycle transition requiring dependent impact enforcement.</summary>
public enum CapabilityLifecycleOperationKind
{
    /// <summary>Activates a newer or otherwise replacement descriptor and immutable artifact.</summary>
    Upgrade = 1,
    /// <summary>Restores the immediately preceding proved descriptor and immutable artifact.</summary>
    Rollback = 2,
    /// <summary>Disables use without deleting identity or provenance.</summary>
    Disable = 3,
    /// <summary>Tombstones the capability while preserving history and provenance.</summary>
    Remove = 4,
    /// <summary>Enables the current proved descriptor and immutable artifact without changing authority.</summary>
    Enable = 5
}
