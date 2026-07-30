namespace EmbodySense.Core.Common.Governance.Permissions.Models;

/// <summary>
/// Identifies the supported file system operation values.
/// </summary>
public enum FileSystemOperation
{
    /// <summary>
    /// Identifies the list file system operation.
    /// </summary>
    List,
    /// <summary>
    /// Identifies the read file system operation.
    /// </summary>
    Read,
    /// <summary>
    /// Identifies the create file system operation.
    /// </summary>
    Create,
    /// <summary>
    /// Identifies the append file system operation.
    /// </summary>
    Append,
    /// <summary>
    /// Identifies the modify file system operation.
    /// </summary>
    Modify,
    /// <summary>
    /// Identifies the delete file system operation.
    /// </summary>
    Delete
}
