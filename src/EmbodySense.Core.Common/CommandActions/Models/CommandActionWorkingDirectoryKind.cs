namespace EmbodySense.Core.Common.CommandActions.Models;

/// <summary>Identifies the server-owned process working-directory class.</summary>
public enum CommandActionWorkingDirectoryKind
{
    /// <summary>No working-directory class was supplied.</summary>
    Unknown = 0,
    /// <summary>The exact retained immutable artifact root is used.</summary>
    ArtifactRoot = 1,
}
