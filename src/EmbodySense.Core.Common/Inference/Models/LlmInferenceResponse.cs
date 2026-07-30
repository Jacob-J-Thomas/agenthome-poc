namespace EmbodySense.Core.Common.Inference.Models;

/// <summary>
/// Represents an LLM inference response.
/// </summary>
/// <param name="OutputText">The output text.</param>
/// <param name="Surface">The normalized owning runtime surface.</param>
/// <param name="Model">The model.</param>
/// <param name="ProviderResponseId">The provider response ID.</param>
public sealed record LlmInferenceResponse(
    string OutputText,
    LlmInferenceSurface Surface,
    string? Model = null,
    string? ProviderResponseId = null);
