namespace EmbodySense.Core.Startup.Loops.Models;

/// <summary>
/// Identifies the supported loop tool assignment values.
/// </summary>
public enum LoopToolAssignment
{
    /// <summary>
    /// No supported workspace command was selected.
    /// </summary>
    Unknown = 0,
    /// <summary>
    /// List governed workspace directory entries.
    /// </summary>
    List = 1,
    /// <summary>
    /// Read a governed workspace file.
    /// </summary>
    Read = 2,
    /// <summary>
    /// Search governed workspace text.
    /// </summary>
    Search = 3,
    /// <summary>
    /// Append to a governed workspace file; not currently custom-assignable.
    /// </summary>
    Append = 4,
    /// <summary>
    /// Replace a governed workspace file; not currently custom-assignable.
    /// </summary>
    Write = 5,
    /// <summary>
    /// Delete a governed workspace file; not currently custom-assignable.
    /// </summary>
    Delete = 6
}
