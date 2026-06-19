namespace EmbodySense.Core.Application.Inference.Models;

public sealed record LlmInferenceResponse(
    string OutputText,
    LlmInferenceSurface Surface,
    string? Model = null,
    string? ProviderResponseId = null);
