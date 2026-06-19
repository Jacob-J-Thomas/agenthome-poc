using EmbodySense.Core.Application.Inference;
using EmbodySense.Core.Application.Inference.Models;

namespace EmbodySense.Core.Startup.Inference;

internal sealed class NotSupportedInferenceClient : ILlmInferenceClient
{
    private readonly string _message;

    public NotSupportedInferenceClient(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        _message = message;
    }

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
