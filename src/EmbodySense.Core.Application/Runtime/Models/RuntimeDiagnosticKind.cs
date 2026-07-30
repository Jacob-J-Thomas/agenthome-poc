namespace EmbodySense.Core.Application.Runtime.Models;

/// <summary>
/// Identifies the supported runtime diagnostic kind values.
/// </summary>
public enum RuntimeDiagnosticKind
{
    /// <summary>
    /// Identifies the unknown runtime diagnostic kind.
    /// </summary>
    Unknown = 0,
    /// <summary>
    /// Identifies the verbose context runtime diagnostic kind.
    /// </summary>
    VerboseContext,
    /// <summary>
    /// Identifies the status runtime diagnostic kind.
    /// </summary>
    Status
}
