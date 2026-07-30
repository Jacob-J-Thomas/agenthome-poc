using EmbodySense.Core.Application.Inference;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Application.Governance.Audit;
using EmbodySense.Core.Clients.CodexAppServer;
using EmbodySense.Core.Application.Governance.Tools;

namespace EmbodySense.Core.Startup.Inference;

internal static class LlmInferenceClientFactory
{
    /// <summary>
    /// Creates the concrete provider selected by the startup options.
    /// </summary>
    /// <param name="options">The provider selection and runtime configuration.</param>
    /// <param name="toolBroker">The optional governed tool broker supplied to Codex app-server.</param>
    /// <param name="codexAppServerTransport">An optional Codex app-server transport override.</param>
    /// <param name="auditLog">The optional audit sink supplied to the provider.</param>
    /// <param name="providerRequestStarted">An optional provider-start callback.</param>
    /// <returns>
    /// The configured provider client, or a deterministic client that reports the selected surface
    /// as unsupported when its adapter is not implemented.
    /// </returns>
    public static ILlmInferenceClient CreateProvider(
        LlmInferenceClientOptions options,
        IToolBroker? toolBroker = null,
        ICodexAppServerTransport? codexAppServerTransport = null,
        IAuditLog? auditLog = null,
        Action? providerRequestStarted = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateSurface(options.Surface);

        return options.Surface switch
        {
            LlmInferenceSurface.OpenAiCodex => new CodexAppServerInferenceClient(options, toolBroker, codexAppServerTransport, auditLog, providerRequestStarted),
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
