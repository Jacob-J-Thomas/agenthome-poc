namespace EmbodySense.Core.Startup.Configuration.Models;

/// <summary>
/// Summarizes workspace initialization and unmatched permission behavior.
/// </summary>
/// <param name="RootPath">The normalized absolute workspace root.</param>
/// <param name="Initialized">Whether the agent directory, role document, and permissions document all exist.</param>
/// <param name="DefaultAccess">A human-readable explanation of fail-closed default permission behavior.</param>
public sealed record WorkspaceConfigurationStatus(
    string RootPath,
    bool Initialized,
    string DefaultAccess);
