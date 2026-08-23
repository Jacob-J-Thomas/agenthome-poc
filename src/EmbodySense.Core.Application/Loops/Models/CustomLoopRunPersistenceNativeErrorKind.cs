namespace EmbodySense.Core.Application.Loops.Models;

/// <summary>Identifies the native error-code namespace retained by a run-store persistence diagnostic.</summary>
public enum CustomLoopRunPersistenceNativeErrorKind
{
    /// <summary>No native error code was available.</summary>
    None = 0,

    /// <summary>The code is the low Win32 error code carried by the failing exception.</summary>
    Win32 = 1,
}
