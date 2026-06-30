using EmbodySense.Core.Application.Inference;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Application.Governance.Audit;
using EmbodySense.Core.Clients.CodexAppServer;
using EmbodySense.Core.Application.Governance.Tools;

namespace EmbodySense.Core.Startup.Inference;

internal static class LlmInferenceClientFactory
{
    public static ILlmInferenceClient CreateProvider(
        LlmInferenceClientOptions options,
        IToolBroker? toolBroker = null,
        ICodexAppServerTransport? codexAppServerTransport = null,
        IAuditLog? auditLog = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateSurface(options.Surface);

        return options.Surface switch
        {
            LlmInferenceSurface.OpenAiCodex => new CodexAppServerInferenceClient(options, toolBroker, codexAppServerTransport, auditLog),
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
