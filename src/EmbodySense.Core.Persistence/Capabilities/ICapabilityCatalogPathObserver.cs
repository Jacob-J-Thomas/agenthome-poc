namespace EmbodySense.Core.Persistence.Capabilities;

/// <summary>Observes bounded retained-handle child opens for deterministic host diagnostics and race testing.</summary>
/// <remarks>The observer is server-owned, runs synchronously under catalog authority, and receives no file handles or mutation authority.</remarks>
public interface ICapabilityCatalogPathObserver
{
    /// <summary>Runs immediately before one child is opened relative to its already-retained parent handle.</summary>
    /// <param name="parentPath">The canonical diagnostic path for the retained parent.</param>
    /// <param name="childName">The single child name that will be opened relative to retained authority.</param>
    void BeforeDirectoryChildOpen(string parentPath, string childName);

    /// <summary>Runs immediately before one regular file is opened relative to its already-retained parent handle.</summary>
    /// <param name="parentPath">The canonical diagnostic path for the retained parent.</param>
    /// <param name="childName">The single child name that will be opened relative to retained authority.</param>
    void BeforeFileChildOpen(string parentPath, string childName);

    /// <summary>Runs after one regular-file open attempt, while any successfully opened handle remains retained.</summary>
    /// <param name="parentPath">The canonical diagnostic path for the retained parent.</param>
    /// <param name="childName">The single child name opened relative to retained authority.</param>
    void AfterFileChildOpen(string parentPath, string childName);
}
