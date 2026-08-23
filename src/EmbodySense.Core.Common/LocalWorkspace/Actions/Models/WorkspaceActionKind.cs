namespace EmbodySense.Core.Common.LocalWorkspace.Actions.Models;

/// <summary>Identifies one closed schema-1 workspace mutation.</summary>
public enum WorkspaceActionKind
{
    /// <summary>No supported action was selected.</summary>
    Unknown = 0,

    /// <summary>Appends exact admitted bytes to one file.</summary>
    Append = 1,

    /// <summary>Replaces or creates one file with exact admitted bytes.</summary>
    Write = 2,

    /// <summary>Moves one file into recoverable quarantine.</summary>
    Delete = 3,
}
