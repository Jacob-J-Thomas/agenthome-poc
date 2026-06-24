namespace EmbodySense.Core.Startup.Configuration;

public sealed record WorkspaceConfigurationPath(
    string Name,
    string Category,
    string Path,
    bool Exists,
    string Description);
