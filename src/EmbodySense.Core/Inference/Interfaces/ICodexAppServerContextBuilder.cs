using EmbodySense.Core.Inference.Models;

namespace EmbodySense.Core.Inference.Interfaces;

internal interface ICodexAppServerContextBuilder
{
    string CreateDeveloperInstructions(LlmInferenceRequest request);
}
