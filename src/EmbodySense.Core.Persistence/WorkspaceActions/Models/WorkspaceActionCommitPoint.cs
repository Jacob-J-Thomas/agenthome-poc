namespace EmbodySense.Core.Persistence.WorkspaceActions.Models;

/// <summary>Names the final race-injection point inside the durable boundary and before native target mutation.</summary>
public enum WorkspaceActionCommitPoint
{
    /// <summary>No commit point was selected.</summary>
    Unknown = 0,

    /// <summary>The exact install precondition was revalidated immediately before the platform-native target mutation.</summary>
    BeforeInstallTargetMutation = 1,

    /// <summary>The exact delete precondition was revalidated immediately before atomic quarantine movement.</summary>
    BeforeDeleteNamespaceMutation = 2,
}
