using EmbodySense.Cli.Common.Enums;

namespace EmbodySense.Cli.Inference.Models;

internal sealed record LlmInferenceResponse(
    string OutputText,
    LlmInferenceSurface Surface,
    string? Model = null,
    string? ProviderResponseId = null);
