namespace EmbodySense.Core.Common.LocalWorkspace.Actions.Models;

/// <summary>Identifies the only target entry postures admitted by schema 1.</summary>
public enum WorkspaceActionEntryKind
{
    /// <summary>No supported entry posture was selected.</summary>
    Unknown = 0,

    /// <summary>The exact target was proved absent under a retained parent.</summary>
    Absent = 1,

    /// <summary>The exact target is a retained, single-link regular file.</summary>
    RegularFile = 2,
}
