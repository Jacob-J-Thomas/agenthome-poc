using EmbodySense.Core.Common.Inference.Models;

namespace EmbodySense.Core.Application.Runtime;

public sealed class InferenceRequestBuilder
{
    // TODO(inference-request-builder): Remove this class or move it beside loop execution unless it grows real request policy.
    // Deferred to keep the current cutover narrow; revisit when context injection, loop identity, or model-routing logic changes request construction.
    public LlmInferenceRequest BuildRequest(IReadOnlyList<LlmMessage> currentMessages, LlmMessage userMessage)
    {
        ArgumentNullException.ThrowIfNull(currentMessages);
        ArgumentNullException.ThrowIfNull(userMessage);

        return new LlmInferenceRequest(currentMessages.Concat([userMessage]).ToArray());
    }
}
