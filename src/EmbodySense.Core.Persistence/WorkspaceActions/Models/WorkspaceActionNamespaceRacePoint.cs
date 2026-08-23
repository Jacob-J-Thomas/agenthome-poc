namespace EmbodySense.Core.Persistence.WorkspaceActions.Models;

/// <summary>Names the exact before/after windows around a platform-native workspace namespace operation.</summary>
public enum WorkspaceActionNamespaceRacePoint
{
    /// <summary>No namespace race point was selected.</summary>
    Unknown = 0,

    /// <summary>All retained install evidence was revalidated immediately before the native system call.</summary>
    BeforeInstallSystemCall = 1,

    /// <summary>The install system call returned before winner identity and ancestor namespace proof.</summary>
    AfterInstallSystemCall = 2,

    /// <summary>All retained delete evidence was revalidated immediately before the native system call.</summary>
    BeforeDeleteSystemCall = 3,

    /// <summary>The delete system call returned before quarantine winner and ancestor namespace proof.</summary>
    AfterDeleteSystemCall = 4,

    /// <summary>A Windows replacement backup hard link was created before the retained stage is renamed into the target namespace.</summary>
    AfterWindowsReplacementBackupLinkBeforeInstallSystemCall = 5,

    /// <summary>An orphan-cleanup target handle was released before its Windows replacement backup is reacquired as the namespace fence.</summary>
    AfterWindowsReplacementTargetReleaseBeforeBackupFence = 6,
}
