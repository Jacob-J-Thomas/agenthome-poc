namespace EmbodySense.Core.Common.LocalWorkspace.Actions.Models;

/// <summary>Identifies one exact optimistic workspace precondition.</summary>
public enum WorkspaceActionPreconditionKind
{
    /// <summary>No supported precondition was selected.</summary>
    Unknown = 0,

    /// <summary>The target must be absent beneath the retained parent.</summary>
    ExpectedAbsent = 1,

    /// <summary>The existing target must have the exact expected content hash.</summary>
    ExpectedContentHash = 2,

    /// <summary>The existing target must match one exact prior governed after-evidence version.</summary>
    ExpectedGovernedVersion = 3,
}
