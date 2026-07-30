using EmbodySense.Core.Clients.CodexAppServer;
using EmbodySense.Core.Clients.CodexAppServer.Models;
using EmbodySense.Core.Startup.Runtime.Models;

namespace EmbodySense.Core.Startup.Runtime;

public sealed class CodexRuntimeStatusReader
{
    public async Task<CodexRuntimeStatus> ReadAsync(string? explicitExecutablePath, string? configuredModel, CancellationToken cancellationToken = default)
    {
        var resolution = await new CodexRuntimeResolver().ResolveAsync(explicitExecutablePath, configuredModel, cancellationToken);
        return new CodexRuntimeStatus(
            MapCompatibility(resolution.Status),
            resolution.ExecutablePath,
            resolution.Version,
            resolution.ConfiguredModel,
            resolution.Source,
            resolution.Detail);
    }

    private static CodexRuntimeCompatibility MapCompatibility(CodexRuntimeResolutionStatus status)
    {
        return status switch
        {
            CodexRuntimeResolutionStatus.Compatible => CodexRuntimeCompatibility.Compatible,
            CodexRuntimeResolutionStatus.ExecutableNotFound => CodexRuntimeCompatibility.ExecutableNotFound,
            CodexRuntimeResolutionStatus.ProbeFailed => CodexRuntimeCompatibility.ProbeFailed,
            CodexRuntimeResolutionStatus.ModelUnavailable => CodexRuntimeCompatibility.ModelUnavailable,
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unsupported Codex runtime resolution status.")
        };
    }
}
