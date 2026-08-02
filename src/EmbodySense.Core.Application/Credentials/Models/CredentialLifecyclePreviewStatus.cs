namespace EmbodySense.Core.Application.Credentials.Models;

/// <summary>Describes whether an exact credential impact preview may be confirmed.</summary>
public enum CredentialLifecyclePreviewStatus
{
    /// <summary>The preview is complete and exact.</summary>
    Ready = 1,
    /// <summary>The exact preview operation was replayed.</summary>
    Replayed = 2,
    /// <summary>The request conflicted with current registry state.</summary>
    Conflict = 3,
    /// <summary>The request was structurally invalid or not previewable.</summary>
    Invalid = 4,
    /// <summary>The actor was not authenticated as a user.</summary>
    Denied = 5,
    /// <summary>The reference does not exist.</summary>
    NotFound = 6,
    /// <summary>The complete dependent set or registry was unavailable.</summary>
    Unavailable = 7
}
