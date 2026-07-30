using EmbodySense.Core.Common.Inference;
using EmbodySense.Core.Common.Inference.Models;

namespace EmbodySense.Core.Application.Inference;

/// <summary>
/// Sends a fully assembled inference request to the configured model provider.
/// </summary>
public interface ILlmInferenceClient
{
    /// <summary>
    /// Generates one response and optionally streams ordered text deltas.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="responseChunkHandler">An optional callback invoked for each ordered response delta.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The completed response and provider metadata.</returns>
    Task<LlmInferenceResponse> GenerateAsync(
        LlmInferenceRequest request,
        Func<string, CancellationToken, Task>? responseChunkHandler = null,
        CancellationToken cancellationToken = default);
}
