namespace EmbodySense.Core.Common.Inference.Models;

public sealed record LlmInferenceRequest
{
    public LlmInferenceRequest(IReadOnlyList<LlmMessage> messages, LlmInferenceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(messages);

        if (messages.Count == 0)
        {
            throw new ArgumentException(
                "At least one message is required for LLM inferencing.",
                nameof(messages));
        }

        Messages = messages.ToArray();
        Options = options ?? LlmInferenceOptions.Default;
    }

    public IReadOnlyList<LlmMessage> Messages { get; }

    public LlmInferenceOptions Options { get; }

    public static LlmInferenceRequest FromUserText(string text, LlmInferenceOptions? options = null)
    {
        return new LlmInferenceRequest([LlmMessage.User(text)], options);
    }
}
