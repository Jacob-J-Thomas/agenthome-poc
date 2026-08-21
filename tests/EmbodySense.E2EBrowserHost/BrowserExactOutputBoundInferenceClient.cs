using EmbodySense.Core.Application.Inference;
using EmbodySense.Core.Common.Inference;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Common.Inference.Profiles.Models;

namespace EmbodySense.E2EBrowserHost;

internal sealed class BrowserExactOutputBoundInferenceClient : ILlmInferenceClient
{
    public Task<LlmInferenceResponse> GenerateAsync(
        LlmInferenceRequest request,
        Func<string, CancellationToken, Task>? responseChunkHandler = null,
        CancellationToken cancellationToken = default)
        => GenerateCoreAsync(request, responseChunkHandler, cancellationToken);

    public Task<LlmInferenceResponse> GenerateAsync(
        LlmInferenceRequest request,
        Func<string, CancellationToken, Task>? responseChunkHandler,
        CancellationToken cancellationToken,
        InferenceProviderTransportCommitBoundary providerTransportCommitBoundary)
        => GenerateCoreAsync(request, responseChunkHandler, cancellationToken, providerTransportCommitBoundary);

    private static async Task<LlmInferenceResponse> GenerateCoreAsync(
        LlmInferenceRequest request,
        Func<string, CancellationToken, Task>? responseChunkHandler,
        CancellationToken cancellationToken,
        InferenceProviderTransportCommitBoundary? providerTransportCommitBoundary = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Options.MaxOutputTokenCount != 1)
        {
            throw new InvalidOperationException("The browser E2E exact adapter requires the governed one-token output ceiling.");
        }

        if (providerTransportCommitBoundary is not null)
        {
            await providerTransportCommitBoundary(_ => Task.CompletedTask, cancellationToken).ConfigureAwait(false);
        }

        const string Output = "browser exact bounded response";
        if (responseChunkHandler is not null)
        {
            await responseChunkHandler(Output, cancellationToken).ConfigureAwait(false);
        }

        return new LlmInferenceResponse(
            Output,
            LlmInferenceSurface.OpenAiCodex,
            LlmInferenceUsageEvidence.Create(
                1,
                "browser-e2e-exact-adapter",
                "v1",
                GovernedModelUsageMeasurement.Authoritative(1),
                GovernedModelUsageMeasurement.Authoritative(1),
                GovernedModelUsageMeasurement.Authoritative(0),
                GovernedModelUsageMeasurement.Authoritative(2),
                GovernedModelMonetaryUsageMeasurement.Unavailable),
            "browser-e2e-model",
            "browser-e2e-response",
            "openai");
    }
}
