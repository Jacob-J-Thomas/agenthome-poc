using EmbodySense.Core.Common.Inference;
using EmbodySense.Core.Application.Inference;
using EmbodySense.Core.Common.Inference.Models;

namespace EmbodySense.Core.Startup.Inference;

internal sealed class NotSupportedInferenceClient : ILlmInferenceClient
{
    private readonly string _message;

    /// <summary>
    /// Initializes a deterministic client for a selected provider surface whose adapter is unavailable.
    /// </summary>
    /// <param name="message">The diagnostic returned for every non-canceled request.</param>
    public NotSupportedInferenceClient(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        _message = message;
    }

    /// <summary>
    /// Returns a canceled task when cancellation was already requested; otherwise returns a faulted
    /// task containing the configured <see cref="NotSupportedException"/>.
    /// </summary>
    /// <param name="request">The request, validated for null but never sent to a provider.</param>
    /// <param name="responseChunkHandler">An unused streaming callback.</param>
    /// <param name="cancellationToken">The token whose already-canceled state takes precedence over the unsupported-provider error.</param>
    /// <returns>A canceled or faulted response task.</returns>
    public Task<LlmInferenceResponse> GenerateAsync(
        LlmInferenceRequest request,
        Func<string, CancellationToken, Task>? responseChunkHandler = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return cancellationToken.IsCancellationRequested
            ? Task.FromCanceled<LlmInferenceResponse>(cancellationToken)
            : Task.FromException<LlmInferenceResponse>(new NotSupportedException(_message));
    }
}
