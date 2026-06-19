using EmbodySense.Core.Application.Inference.Models;

namespace EmbodySense.Core.Application.Inference;

public interface ILlmInferenceClient
{
    Task<LlmInferenceResponse> GenerateAsync(
        LlmInferenceRequest request,
        Func<string, CancellationToken, Task>? responseChunkHandler = null,
        CancellationToken cancellationToken = default);
}
