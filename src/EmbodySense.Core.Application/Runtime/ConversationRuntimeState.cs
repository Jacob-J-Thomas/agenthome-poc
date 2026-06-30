using EmbodySense.Core.Application.Inference;
using EmbodySense.Core.Common.Inference.Models;

namespace EmbodySense.Core.Application.Runtime;

public sealed class ConversationRuntimeState
{
    private readonly IResettableInferenceClient? _resettableInferenceClient;
    private readonly List<LlmMessage> _messages;

    public ConversationRuntimeState(
        IReadOnlyList<LlmMessage>? initialMessages = null,
        IResettableInferenceClient? resettableInferenceClient = null)
    {
        _resettableInferenceClient = resettableInferenceClient;
        _messages = initialMessages?.ToList() ?? [];
    }

    public IReadOnlyList<LlmMessage> Messages => _messages;

    public void AppendMessage(LlmMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        _messages.Add(message);
    }

    public void ReplaceMessages(IReadOnlyList<LlmMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        _messages.Clear();
        _messages.AddRange(messages);
        _resettableInferenceClient?.ResetConversation();
    }
}
