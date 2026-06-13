using EmbodySense.Core.Inference.Interfaces;
using EmbodySense.Core.Inference.Models;

namespace EmbodySense.Core.Inference.Implementations;

internal static class LlmInferenceClientFactory
{
    public static ILlmInferenceClient CreateProvider(LlmInferenceClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateSurface(options.Surface);

        return options.Surface switch
        {
            LlmInferenceSurface.OpenAiCodex => new CodexCliInferenceClient(options),
            LlmInferenceSurface.AzureAiFoundry => new NotSupportedInferenceClient(
                "Azure AI Foundry inferencing is selected, but the Azure adapter has not been wired yet."),
            _ => new NotSupportedInferenceClient("LLM inferencing is not wired for the selected surface.")
        };
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
