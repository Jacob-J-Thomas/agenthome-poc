namespace EmbodySense.Core.Common.LocalWorkspace.Actions.Models;

/// <summary>Identifies the two conclusive schema-1 workspace action result postures.</summary>
public enum WorkspaceActionResultStatus
{
    /// <summary>No supported result posture was selected.</summary>
    Unknown = 0,

    /// <summary>The exact workspace outcome was committed by this invocation.</summary>
    Committed = 1,

    /// <summary>The exact previously committed workspace outcome was replayed without mutation.</summary>
    Replayed = 2,
}
