using EmbodySense.Core.Common.Inference.Models;

namespace EmbodySense.Core.Clients.CodexAppServer;

internal interface ICodexAppServerContextBuilder
{
    string CreateDeveloperInstructions(LlmInferenceRequest request);

    string CreateTurnInput(LlmInferenceRequest request);
}
