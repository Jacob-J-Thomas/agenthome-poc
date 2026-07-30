namespace EmbodySense.Core.Common.Inference.Models;

/// <summary>
/// Configures the provider client and the runtime surface that owns its inference requests.
/// </summary>
public sealed record LlmInferenceClientOptions
{
    /// <summary>
    /// Gets the required owning inference surface.
    /// </summary>
    /// <value>The LLM inference surface.</value>
    public required LlmInferenceSurface Surface { get; init; }

    /// <summary>
    /// Gets the optional provider model override.
    /// </summary>
    /// <value>The model.</value>
    public string? Model { get; init; }

    /// <summary>
    /// Gets the optional inert runtime working directory used to host the provider process.
    /// </summary>
    /// <value>The working directory.</value>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Gets the optional Codex executable override.
    /// </summary>
    /// <value>The Codex executable path.</value>
    public string? CodexExecutablePath { get; init; }

    /// <summary>
    /// Gets the Codex sandbox mode, which defaults to read-only.
    /// </summary>
    /// <value>The Codex sandbox.</value>
    public string CodexSandbox { get; init; } = "read-only";
}
