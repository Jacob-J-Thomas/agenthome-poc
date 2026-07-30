using EmbodySense.Core.Clients.CodexAppServer;
using EmbodySense.Core.Clients.CodexAppServer.Models;
using EmbodySense.Core.Startup.Runtime.Models;

namespace EmbodySense.Core.Startup.Runtime;

/// <summary>
/// Projects Codex executable and model compatibility through the Core.Startup interface boundary.
/// </summary>
public sealed class CodexRuntimeStatusReader
{
    /// <summary>
    /// Resolves and probes the effective Codex executable for the requested model.
    /// </summary>
    /// <param name="explicitExecutablePath">An optional executable that takes precedence over normal runtime discovery.</param>
    /// <param name="configuredModel">The optional model that the discovered Codex CLI must support.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task whose result describes compatibility, resolution source, version, and diagnostic detail without throwing for an incompatible runtime.</returns>
    public async Task<CodexRuntimeStatus> ReadAsync(string? explicitExecutablePath, string? configuredModel, CancellationToken cancellationToken = default)
    {
        var resolution = await new CodexRuntimeResolver().ResolveAsync(explicitExecutablePath, configuredModel, cancellationToken);
        return new CodexRuntimeStatus(
            MapCompatibility(resolution.Status),
            explicitExecutablePath,
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
