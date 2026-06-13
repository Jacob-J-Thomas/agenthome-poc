using EmbodySense.Core.Inference.Models;

namespace EmbodySense.Core.Inference.Interfaces;

public interface ILlmInferenceClient
{
    Task<LlmInferenceResponse> GenerateAsync(
        LlmInferenceRequest request,
        Func<string, CancellationToken, Task>? responseChunkHandler = null,
        CancellationToken cancellationToken = default);
}
