using EmbodySense.Core.Common.Inference.Models;

namespace EmbodySense.Core.Application.Runtime;

public sealed class InferenceRequestBuilder
{
    public LlmInferenceRequest BuildRequest(IReadOnlyList<LlmMessage> currentMessages, LlmMessage userMessage)
    {
        ArgumentNullException.ThrowIfNull(currentMessages);
        ArgumentNullException.ThrowIfNull(userMessage);

        return new LlmInferenceRequest(currentMessages.Concat([userMessage]).ToArray());
    }
}
