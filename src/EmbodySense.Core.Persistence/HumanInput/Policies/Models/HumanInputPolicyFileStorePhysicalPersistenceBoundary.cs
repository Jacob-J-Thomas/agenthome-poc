namespace EmbodySense.Core.Persistence.HumanInput.Policies.Models;

/// <summary>Identifies physical schema-1 policy-store publication and retirement windows exposed for deterministic crash evaluation.</summary>
public enum HumanInputPolicyFileStorePhysicalPersistenceBoundary
{
    /// <summary>No physical persistence boundary was selected.</summary>
    Unknown = 0,

    /// <summary>The exact sibling temporary file was flushed before publication.</summary>
    StagedFileFlushed = 1,

    /// <summary>The retained-parent rename completed, before the POSIX parent-directory durability barrier.</summary>
    CanonicalRenamed = 2,

    /// <summary>The post-rename adapter completed: a retained parent-directory barrier on POSIX or a reopened-target flush on Windows.</summary>
    ParentDirectoryFlushed = 3,

    /// <summary>The reopened canonical target was proven to retain the staged identity and exact bytes.</summary>
    TargetProven = 4,

    /// <summary>The exact regular file was deleted, before the POSIX parent-directory retirement barrier.</summary>
    Deleted = 5,

    /// <summary>The post-retirement adapter completed: a parent-directory barrier on POSIX; Windows does not infer directory metadata ordering.</summary>
    Retired = 6
}
