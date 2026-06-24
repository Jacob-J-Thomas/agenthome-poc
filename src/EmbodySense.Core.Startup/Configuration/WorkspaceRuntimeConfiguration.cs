namespace EmbodySense.Core.Startup.Configuration;

public sealed record WorkspaceRuntimeConfiguration(
    string Surface,
    string Url,
    string Model,
    string CodexExecutablePath,
    string CodexSandbox,
    string Notes);
