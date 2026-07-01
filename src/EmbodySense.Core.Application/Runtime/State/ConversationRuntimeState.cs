using EmbodySense.Core.Application.Inference;
using EmbodySense.Core.Application.Runtime.Models;
using EmbodySense.Core.Common.Inference.Models;

namespace EmbodySense.Core.Application.Runtime.State;

public sealed class ConversationRuntimeState
{
    private readonly IResettableInferenceClient? _resettableInferenceClient;
    private readonly List<RuntimeContextMessage> _messages;

    public ConversationRuntimeState(
        IReadOnlyList<LlmMessage>? initialMessages = null,
        IResettableInferenceClient? resettableInferenceClient = null)
    {
        _resettableInferenceClient = resettableInferenceClient;
        _messages = initialMessages?.Select(message => CreateContextMessage(message, RuntimeContextSource.StartupContext)).ToList() ?? [];
    }

    public IReadOnlyList<LlmMessage> Messages => _messages.Select(message => message.Message).ToArray();

    public IReadOnlyList<RuntimeContextMessage> ContextMessages => _messages;

    public void AppendMessage(LlmMessage message, RuntimeContextSource source = RuntimeContextSource.SessionTranscript)
    {
        ArgumentNullException.ThrowIfNull(message);

        _messages.Add(CreateContextMessage(message, source));
    }

    public void ReplaceMessages(
        IReadOnlyList<LlmMessage> messages,
        int startupContextCount = 0,
        RuntimeContextSource remainingSource = RuntimeContextSource.SessionTranscript,
        string? remainingDetail = null)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (startupContextCount < 0 || startupContextCount > messages.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(startupContextCount), startupContextCount, "Startup context count must fit the replacement message list.");
        }

        _messages.Clear();
        for (var i = 0; i < messages.Count; i++)
        {
            var source = i < startupContextCount ? RuntimeContextSource.StartupContext : remainingSource;
            var detail = i < startupContextCount ? null : remainingDetail;
            _messages.Add(CreateContextMessage(messages[i], source, detail));
        }

        _resettableInferenceClient?.ResetConversation();
    }

    private static RuntimeContextMessage CreateContextMessage(LlmMessage message, RuntimeContextSource source, string? detail = null)
    {
        return new RuntimeContextMessage(message, source, detail ?? GetDefaultDetail(source));
    }

    private static string GetDefaultDetail(RuntimeContextSource source)
    {
        return source switch
        {
            RuntimeContextSource.StartupContext => "Loaded during runtime bootstrap from workspace and agent context documents.",
            RuntimeContextSource.RestoredConversationHistory => "Restored from conversation history at the user's request.",
            RuntimeContextSource.SessionTranscript => "Accepted during this runtime session and retained in conversation state.",
            RuntimeContextSource.CurrentTurnInput => "Current user input being evaluated by the active loop before provider dispatch.",
            _ => "Context source is not classified."
        };
    }
}
