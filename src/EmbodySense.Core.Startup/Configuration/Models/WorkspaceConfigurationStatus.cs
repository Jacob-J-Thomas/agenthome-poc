namespace EmbodySense.Core.Startup.Configuration.Models;

public sealed record WorkspaceConfigurationStatus(
    string RootPath,
    bool Initialized,
    string DefaultAccess);
