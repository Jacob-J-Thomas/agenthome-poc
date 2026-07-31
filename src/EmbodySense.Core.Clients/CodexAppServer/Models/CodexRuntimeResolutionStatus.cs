namespace EmbodySense.Core.Clients.CodexAppServer.Models;

/// <summary>
/// Identifies the supported Codex runtime resolution status values.
/// </summary>
public enum CodexRuntimeResolutionStatus
{
    /// <summary>
    /// The executable started app-server and satisfied the configured model requirement.
    /// </summary>
    Compatible,
    /// <summary>
    /// No usable executable path was found.
    /// </summary>
    ExecutableNotFound,
    /// <summary>
    /// Candidates were found, but none completed the app-server compatibility probe.
    /// </summary>
    ProbeFailed,
    /// <summary>
    /// A runtime was probed, but none advertised the configured model.
    /// </summary>
    ModelUnavailable
}
