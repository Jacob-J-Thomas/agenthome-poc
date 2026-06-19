using EmbodySense.Core.Application.Inference.Models;

namespace EmbodySense.Core.Clients.CodexAppServer;

internal interface ICodexAppServerContextBuilder
{
    string CreateDeveloperInstructions(LlmInferenceRequest request);
}
