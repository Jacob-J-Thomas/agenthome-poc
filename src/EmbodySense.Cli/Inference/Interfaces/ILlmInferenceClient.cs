using EmbodySense.Cli.Inference.Models;

namespace EmbodySense.Cli.Inference.Interfaces;

internal interface ILlmInferenceClient
{
    Task<LlmInferenceResponse> GenerateAsync(
        LlmInferenceRequest request,
        CancellationToken cancellationToken = default);
}
