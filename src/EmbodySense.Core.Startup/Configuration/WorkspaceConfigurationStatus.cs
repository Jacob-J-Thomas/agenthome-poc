namespace EmbodySense.Core.Startup.Configuration;

public sealed record WorkspaceConfigurationStatus(
    string RootPath,
    bool Initialized,
    string DefaultAccess);
