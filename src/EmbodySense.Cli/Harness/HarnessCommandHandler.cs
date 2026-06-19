using System.Globalization;
using EmbodySense.Core.Harness;
using EmbodySense.Core.Inference.Models;
using EmbodySense.Core.Memory;

namespace EmbodySense.Cli.Harness;

internal sealed class HarnessCommandHandler
{
    private const int ConversationPromptPreviewLength = 96;
    private readonly ConversationMemoryStore? _conversationMemoryStore;
    private readonly IReadOnlyList<LlmMessage> _startupMessages;

    public HarnessCommandHandler(
        ConversationMemoryStore? conversationMemoryStore = null,
        IReadOnlyList<LlmMessage>? startupMessages = null)
    {
        _conversationMemoryStore = conversationMemoryStore;
        _startupMessages = startupMessages ?? [];
    }

    public async Task<HarnessCommandResult> TryHandleAsync(
        string input,
        AgentHarnessSession session,
        bool modelTurnStarted,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        ArgumentNullException.ThrowIfNull(session);

        var command = input.Trim().ToLowerInvariant();
        return command switch
        {
            "exit" or "quit" or "/exit" or "/quit" => HarnessCommandResult.ExitRequested,
            "/help" or "/commands" => HandleHelpCommand(),
            "/new" or "/new-session" => await HandleNewSessionAsync(session, cancellationToken),
            "/history" or "/conversations" or "/load" => await HandleConversationLoadAsync(session, modelTurnStarted, cancellationToken),
            _ => HarnessCommandResult.NotHandled
        };
    }

    private static HarnessCommandResult HandleHelpCommand()
    {
        Console.WriteLine("Harness commands:");
        Console.WriteLine("/help, /commands - list harness commands");
        Console.WriteLine("/new, /new-session - start a fresh conversation without leaving the harness");
        Console.WriteLine("/history, /conversations, /load - load a saved conversation before the first prompt in the current session");
        Console.WriteLine("/exit, /quit - leave the harness");
        return HarnessCommandResult.Handled;
    }

    private async Task<HarnessCommandResult> HandleNewSessionAsync(
        AgentHarnessSession session,
        CancellationToken cancellationToken)
    {
        if (_conversationMemoryStore is not null)
        {
            await _conversationMemoryStore.StartFreshConversationAsync(cancellationToken);
        }

        session.ReplaceMessages(_startupMessages);
        Console.WriteLine("Started a new conversation.");
        return HarnessCommandResult.NewSessionStarted;
    }

    private async Task<HarnessCommandResult> HandleConversationLoadAsync(
        AgentHarnessSession session,
        bool modelTurnStarted,
        CancellationToken cancellationToken)
    {
        if (modelTurnStarted)
        {
            Console.WriteLine("Load a stored conversation before sending the first prompt in this session. Use /new first to start a fresh session.");
            return HarnessCommandResult.Handled;
        }

        if (_conversationMemoryStore is null)
        {
            Console.WriteLine("Conversation history is not available for this session.");
            return HarnessCommandResult.Handled;
        }

        var conversations = await _conversationMemoryStore.ListConversationsAsync(cancellationToken);
        if (conversations.Count == 0)
        {
            Console.WriteLine("No stored conversations were found.");
            return HarnessCommandResult.Handled;
        }

        Console.WriteLine("Stored conversations:");
        for (var i = 0; i < conversations.Count; i++)
        {
            var conversation = conversations[i];
            var currentMarker = conversation.IsCurrent ? " (current)" : "";
            var timestamp = conversation.LastTimestampUtc.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
            var promptPreview = FormatPromptPreview(conversation.FirstPrompt);
            Console.WriteLine($"{i + 1}. {conversation.ConversationId}{currentMarker} | {conversation.MessageCount} messages | {timestamp} | {promptPreview}");
        }

        Console.Write("Select conversation number to load, or press Enter to cancel: ");
        var answer = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(answer))
        {
            Console.WriteLine("Conversation load cancelled.");
            return HarnessCommandResult.Handled;
        }

        if (!int.TryParse(answer.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var selectedNumber) || selectedNumber < 1 || selectedNumber > conversations.Count)
        {
            Console.WriteLine("Invalid conversation selection.");
            return HarnessCommandResult.Handled;
        }

        var selectedConversation = conversations[selectedNumber - 1];
        try
        {
            var conversationMessages = await _conversationMemoryStore.LoadConversationAsync(selectedConversation.ConversationId, cancellationToken);
            await _conversationMemoryStore.ResumeConversationAsync(selectedConversation.ConversationId, cancellationToken);
            session.ReplaceMessages(_startupMessages.Concat(conversationMessages).ToArray());
            Console.WriteLine($"Loaded conversation `{selectedConversation.ConversationId}` ({conversationMessages.Count} messages).");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException)
        {
            Console.WriteLine($"Could not load conversation: {exception.Message}");
        }

        return HarnessCommandResult.Handled;
    }

    private static string FormatPromptPreview(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return "(no user prompt)";
        }

        var normalizedPrompt = string.Join(" ", prompt.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
        return normalizedPrompt.Length <= ConversationPromptPreviewLength
            ? normalizedPrompt
            : normalizedPrompt[..(ConversationPromptPreviewLength - 3)] + "...";
    }
}
