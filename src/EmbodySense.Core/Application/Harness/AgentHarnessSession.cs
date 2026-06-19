using EmbodySense.Core.Application.Inference;
using EmbodySense.Core.Application.Inference.Models;
using EmbodySense.Core.Application.Memory;

namespace EmbodySense.Core.Application.Harness;

public sealed class AgentHarnessSession
{
    private readonly ILlmInferenceClient _inferenceClient;
    private readonly IConversationMemoryStore? _conversationMemoryStore;
    private readonly List<LlmMessage> _messages;

    public AgentHarnessSession(
        ILlmInferenceClient inferenceClient,
        IConversationMemoryStore? conversationMemoryStore = null,
        IReadOnlyList<LlmMessage>? initialMessages = null)
    {
        ArgumentNullException.ThrowIfNull(inferenceClient);

        _inferenceClient = inferenceClient;
        _conversationMemoryStore = conversationMemoryStore;
        _messages = initialMessages?.ToList() ?? [];
    }

    public IReadOnlyList<LlmMessage> Messages => _messages;

    public void ReplaceMessages(IReadOnlyList<LlmMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        _messages.Clear();
        _messages.AddRange(messages);
        if (_inferenceClient is IResettableInferenceClient resettableClient) resettableClient.ResetConversation();
    }

    public async Task<LlmInferenceResponse> SendUserMessageAsync(
        string input,
        Func<string, CancellationToken, Task>? responseChunkHandler = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);

        var userMessage = LlmMessage.User(input);
        _messages.Add(userMessage);
        if (_conversationMemoryStore is not null) await _conversationMemoryStore.AppendMessageAsync(userMessage, cancellationToken);

        var response = await _inferenceClient.GenerateAsync(new LlmInferenceRequest(_messages), responseChunkHandler, cancellationToken);
        var assistantMessage = LlmMessage.Assistant(response.OutputText);
        _messages.Add(assistantMessage);
        if (_conversationMemoryStore is not null) await _conversationMemoryStore.AppendMessageAsync(assistantMessage, cancellationToken);

        return response;
    }
}
