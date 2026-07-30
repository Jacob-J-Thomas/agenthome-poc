using EmbodySense.Core.Startup.Runtime.Models;

namespace EmbodySense.Core.Startup.Configuration.Models;

public sealed record WorkspaceRuntimeConfiguration(
    string Surface,
    string Url,
    string Model,
    string CodexExecutablePath,
    string CodexSandbox,
    string Notes)
{
    public CodexRuntimeStatus? CodexRuntime { get; init; }
}
