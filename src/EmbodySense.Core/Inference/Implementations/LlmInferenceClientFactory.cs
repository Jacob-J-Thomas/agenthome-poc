using EmbodySense.Core.Inference.Interfaces;
using EmbodySense.Core.Inference.Models;
using EmbodySense.Core.Tools;

namespace EmbodySense.Core.Inference.Implementations;

internal static class LlmInferenceClientFactory
{
    public static ILlmInferenceClient CreateProvider(
        LlmInferenceClientOptions options,
        IToolBroker? toolBroker = null,
        ICodexAppServerTransport? codexAppServerTransport = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateSurface(options.Surface);

        return options.Surface switch
        {
            LlmInferenceSurface.OpenAiCodex => new CodexAppServerInferenceClient(options, toolBroker, codexAppServerTransport),
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
