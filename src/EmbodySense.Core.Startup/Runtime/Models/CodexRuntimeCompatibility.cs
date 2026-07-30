namespace EmbodySense.Core.Startup.Runtime.Models;

/// <summary>
/// Identifies the supported codex runtime compatibility values.
/// </summary>
public enum CodexRuntimeCompatibility
{
    /// <summary>
    /// The executable probe and configured-model availability checks succeeded.
    /// </summary>
    Compatible,
    /// <summary>
    /// No discovered or explicitly requested executable passed resolution.
    /// </summary>
    ExecutableNotFound,
    /// <summary>
    /// An executable was found but its bounded version or model probe failed.
    /// </summary>
    ProbeFailed,
    /// <summary>
    /// The executable is usable but does not advertise the configured model.
    /// </summary>
    ModelUnavailable
}
