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

    /// <summary>The final exact Windows before-image check completed immediately before the ReplaceFileW call.</summary>
    AfterWindowsReplacementFinalCheckBeforeReplaceSystemCall = 5,

    /// <summary>ReplaceFileW returned before the private backup was opened and authenticated.</summary>
    AfterWindowsReplacementSystemCallBeforeBackupRetention = 6,

    /// <summary>An orphan-cleanup payload is retained with write and delete sharing denied before its exact deletion.</summary>
    BeforeCleanupArtifactDelete = 7,

}
