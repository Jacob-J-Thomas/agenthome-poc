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
}
