namespace EmbodySense.Core.Common.Inference.Models;

/// <summary>
/// Identifies the supported LLM inference surface values.
/// </summary>
public enum LlmInferenceSurface
{
    /// <summary>
    /// Identifies the unknown LLM inference surface.
    /// </summary>
    Unknown = 0,
    /// <summary>
    /// Identifies the azure ai foundry LLM inference surface.
    /// </summary>
    AzureAiFoundry = 1,
    /// <summary>
    /// Identifies the open ai Codex LLM inference surface.
    /// </summary>
    OpenAiCodex = 2
}
