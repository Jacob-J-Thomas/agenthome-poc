using EmbodySense.Cli.Inference.Interfaces;
using EmbodySense.Cli.Inference.Models;

namespace EmbodySense.Cli.Inference.Implementations;

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
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return cancellationToken.IsCancellationRequested
            ? Task.FromCanceled<LlmInferenceResponse>(cancellationToken)
            : Task.FromException<LlmInferenceResponse>(new NotSupportedException(_message));
    }
}
