using EmbodySense.Cli.Common.Enums;
using EmbodySense.Cli.Inference.Interfaces;
using EmbodySense.Cli.Inference.Models;

namespace EmbodySense.Cli.Inference.Implementations;

internal sealed class LlmInferenceClient : ILlmInferenceClient
{
    private readonly ILlmInferenceClient _innerClient;

    public LlmInferenceClient(LlmInferenceClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateSurface(options.Surface);

        _innerClient = options.Surface switch
        {
            LlmInferenceSurface.OpenAiCodex => new CodexCliInferenceClient(options),
            LlmInferenceSurface.AzureAiFoundry => new NotSupportedInferenceClient(
                "Azure AI Foundry inferencing is selected, but the Azure adapter has not been wired yet."),
            _ => new NotSupportedInferenceClient("LLM inferencing is not wired for the selected surface.")
        };
    }

    public Task<LlmInferenceResponse> GenerateAsync(
        LlmInferenceRequest request,
        CancellationToken cancellationToken = default)
    {
        return _innerClient.GenerateAsync(request, cancellationToken);
    }

    private static void ValidateSurface(LlmInferenceSurface surface)
    {
        if (!Enum.IsDefined(surface) || surface == LlmInferenceSurface.Unknown)
        {
            throw new ArgumentOutOfRangeException(
                nameof(surface),
                surface,
                "Choose a concrete LLM inference surface.");
        }
    }
}
