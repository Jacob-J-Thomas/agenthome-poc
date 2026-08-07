namespace EmbodySense.Core.Persistence.ContextualRoles.Models;

/// <summary>Identifies security-sensitive physical persistence windows exposed for deterministic race and crash evaluation.</summary>
public enum ContextualRolePhysicalPersistenceBoundary
{
    /// <summary>No physical boundary was selected.</summary>
    Unknown = 0,
    /// <summary>A retained parent directory was validated immediately before a handle-relative read open.</summary>
    BeforeHandleRelativeRead = 1,
    /// <summary>A durable temporary file was validated immediately before its handle-relative publication rename.</summary>
    BeforeHandleRelativePublication = 2,
    /// <summary>The target rename completed and the exact target was flushed, but parent-directory metadata was not yet flushed.</summary>
    AfterTargetFlushBeforeDirectoryFlush = 3,
    /// <summary>A retained directory was validated immediately before its entries were enumerated for persistence validation.</summary>
    BeforeHandleRelativeValidationEnumeration = 4,
    /// <summary>A retained directory was enumerated for persistence validation, before canonical mappings are revalidated.</summary>
    AfterHandleRelativeValidationEnumeration = 5
}
