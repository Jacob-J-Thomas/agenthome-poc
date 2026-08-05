namespace EmbodySense.Core.Application.ContextualRoles.Models;

/// <summary>Identifies the bounded persistence stage that prevented a contextual-role mutation from completing.</summary>
public enum ContextualRolePersistenceDiagnosticStage
{
    /// <summary>No persistence stage was identified.</summary>
    Unknown = 0,
    /// <summary>The guarded persistence roots or retained mappings could not be prepared.</summary>
    RootPreparation = 1,
    /// <summary>The temporary publication file could not be opened by retained parent handle.</summary>
    TemporaryFileOpen = 2,
    /// <summary>The temporary file did not prove the required type, link count, or identity.</summary>
    TemporaryFileIdentityValidation = 3,
    /// <summary>The exact artifact bytes could not be written through the temporary handle.</summary>
    TemporaryFileWrite = 4,
    /// <summary>The temporary file could not complete its native data barrier.</summary>
    TemporaryFileFlush = 5,
    /// <summary>The temporary file changed type, link count, or identity after its data barrier.</summary>
    TemporaryFilePostFlushIdentityValidation = 6,
    /// <summary>The configured pre-publication boundary observer failed.</summary>
    PrePublicationObservation = 7,
    /// <summary>The temporary handle could not be renamed relative to its retained parent directory.</summary>
    PublicationRename = 8,
    /// <summary>The named target could not be reopened relative to its retained parent directory.</summary>
    PublishedTargetOpen = 9,
    /// <summary>The reopened target did not match the temporary file's exact physical identity.</summary>
    PublishedTargetIdentityValidation = 10,
    /// <summary>The reopened target did not contain the exact intended artifact bytes.</summary>
    PublishedTargetContentValidation = 11,
    /// <summary>The reopened target could not complete its native data barrier.</summary>
    PublishedTargetFlush = 12,
    /// <summary>The configured post-target-flush boundary observer failed.</summary>
    PostTargetFlushObservation = 13,
    /// <summary>The retained parent directory could not complete its supported metadata barrier.</summary>
    ParentDirectoryFlush = 14,
    /// <summary>The published target or retained directory mapping changed before acknowledgement.</summary>
    PublishedMappingValidation = 15,
    /// <summary>An unpublished temporary artifact could not be cleaned up safely.</summary>
    TemporaryFileCleanup = 16,
    /// <summary>An artifact existence check failed before a guarded read or publication decision.</summary>
    ArtifactExistenceCheck = 17,
    /// <summary>A retained artifact directory could not be enumerated safely.</summary>
    ArtifactEnumeration = 18
}
