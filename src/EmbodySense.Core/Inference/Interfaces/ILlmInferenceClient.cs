using EmbodySense.Core.Inference.Models;

namespace EmbodySense.Core.Inference.Interfaces;

public interface ILlmInferenceClient
{
    Task<LlmInferenceResponse> GenerateAsync(
        LlmInferenceRequest request,
        CancellationToken cancellationToken = default);
}
