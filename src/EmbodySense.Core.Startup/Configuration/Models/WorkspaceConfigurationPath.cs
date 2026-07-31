namespace EmbodySense.Core.Startup.Configuration.Models;

/// <summary>
/// Describes one canonical workspace location for configuration display.
/// </summary>
/// <param name="Name">The path display name.</param>
/// <param name="Category">The path display category.</param>
/// <param name="Path">The absolute file or directory path.</param>
/// <param name="Exists">Whether either the directory or file existence probe reported true.</param>
/// <param name="Description">The location's intended role.</param>
public sealed record WorkspaceConfigurationPath(
    string Name,
    string Category,
    string Path,
    bool Exists,
    string Description);
