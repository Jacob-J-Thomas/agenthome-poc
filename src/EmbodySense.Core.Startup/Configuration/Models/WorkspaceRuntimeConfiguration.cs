using EmbodySense.Core.Startup.Runtime.Models;

namespace EmbodySense.Core.Startup.Configuration.Models;

/// <summary>
/// Describes the interface-selected runtime configuration displayed with a workspace snapshot.
/// </summary>
/// <param name="Surface">The selected inference surface display value.</param>
/// <param name="Url">The interface or provider URL display value.</param>
/// <param name="Model">The configured model identifier.</param>
/// <param name="CodexExecutablePath">The configured Codex executable path.</param>
/// <param name="CodexSandbox">The configured Codex sandbox mode.</param>
/// <param name="Notes">Interface-owned explanatory runtime notes.</param>
public sealed record WorkspaceRuntimeConfiguration(
    string Surface,
    string Url,
    string Model,
    string CodexExecutablePath,
    string CodexSandbox,
    string Notes)
{
    /// <summary>
    /// Gets the optional live Codex executable and model compatibility status.
    /// </summary>
    public CodexRuntimeStatus? CodexRuntime { get; init; }
}
