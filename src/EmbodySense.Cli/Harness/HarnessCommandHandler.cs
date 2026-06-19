using System.Globalization;
using EmbodySense.Core.Application.Harness;
using EmbodySense.Core.Application.Inference.Models;
using EmbodySense.Core.Application.Memory;

namespace EmbodySense.Cli.Harness;

public sealed class HarnessCommandHandler
{
    private const int ConversationPromptPreviewLength = 96;
    private readonly IConversationMemoryStore? _conversationMemoryStore;
    private readonly IReadOnlyList<LlmMessage> _startupMessages;
    private readonly IHarnessTerminal _terminal;

    public HarnessCommandHandler(
        IConversationMemoryStore? conversationMemoryStore = null,
        IReadOnlyList<LlmMessage>? startupMessages = null,
        IHarnessTerminal? terminal = null)
    {
        _conversationMemoryStore = conversationMemoryStore;
        _startupMessages = startupMessages ?? [];
        _terminal = terminal ?? ConsoleHarnessTerminal.Instance;
    }

    public async Task<bool> TryHandleAsync(
        string input,
        AgentHarnessSession session,
        HarnessLoopState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(state);

        var command = input.Trim().ToLowerInvariant();
        switch (command)
        {
            case "exit" or "quit" or "/exit" or "/quit":
                state.RequestExit();
                return true;

            case "/help" or "/commands":
                HandleHelpCommand();
                return true;

            case "/new" or "/new-session":
                await HandleNewSessionAsync(session, state, cancellationToken);
                return true;

            case "/history" or "/conversations" or "/load":
                await HandleConversationLoadAsync(session, state, cancellationToken);
                return true;

            default:
                return false;
        }
    }

    private void HandleHelpCommand()
    {
        _terminal.WriteLine("Harness commands:");
        _terminal.WriteLine("/help, /commands - list harness commands");
        _terminal.WriteLine("/new, /new-session - start a fresh conversation without leaving the harness");
        _terminal.WriteLine("/history, /conversations, /load - load a saved conversation before the first prompt in the current session");
        _terminal.WriteLine("/exit, /quit - leave the harness");
    }

    private async Task HandleNewSessionAsync(
        AgentHarnessSession session,
        HarnessLoopState state,
        CancellationToken cancellationToken)
    {
        if (_conversationMemoryStore is not null)
        {
            await _conversationMemoryStore.StartFreshConversationAsync(cancellationToken);
        }

        session.ReplaceMessages(_startupMessages);
        state.ResetModelTurn();
        _terminal.WriteLine("Started a new conversation.");
    }

    private async Task HandleConversationLoadAsync(
        AgentHarnessSession session,
        HarnessLoopState state,
        CancellationToken cancellationToken)
    {
        if (state.ModelTurnStarted)
        {
            _terminal.WriteLine("Load a stored conversation before sending the first prompt in this session. Use /new first to start a fresh session.");
            return;
        }

        if (_conversationMemoryStore is null)
        {
            _terminal.WriteLine("Conversation history is not available for this session.");
            return;
        }

        var conversations = await _conversationMemoryStore.ListConversationsAsync(cancellationToken);
        if (conversations.Count == 0)
        {
            _terminal.WriteLine("No stored conversations were found.");
            return;
        }

        _terminal.WriteLine("Stored conversations:");
        for (var i = 0; i < conversations.Count; i++)
        {
            var conversation = conversations[i];
            var currentMarker = conversation.IsCurrent ? " (current)" : "";
            var timestamp = conversation.LastTimestampUtc.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
            var promptPreview = FormatPromptPreview(conversation.FirstPrompt);
            _terminal.WriteLine($"{i + 1}. {conversation.ConversationId}{currentMarker} | {conversation.MessageCount} messages | {timestamp} | {promptPreview}");
        }

        _terminal.Write("Select conversation number to load, or press Enter to cancel: ");
        var answer = _terminal.ReadLine();
        if (string.IsNullOrWhiteSpace(answer))
        {
            _terminal.WriteLine("Conversation load cancelled.");
            return;
        }

        if (!int.TryParse(answer.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var selectedNumber) || selectedNumber < 1 || selectedNumber > conversations.Count)
        {
            _terminal.WriteLine("Invalid conversation selection.");
            return;
        }

        var selectedConversation = conversations[selectedNumber - 1];
        try
        {
            var conversationMessages = await _conversationMemoryStore.LoadConversationAsync(selectedConversation.ConversationId, cancellationToken);
            await _conversationMemoryStore.ResumeConversationAsync(selectedConversation.ConversationId, cancellationToken);
            session.ReplaceMessages(_startupMessages.Concat(conversationMessages).ToArray());
            _terminal.WriteLine($"Loaded conversation `{selectedConversation.ConversationId}` ({conversationMessages.Count} messages).");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException)
        {
            _terminal.WriteLine($"Could not load conversation: {exception.Message}");
        }
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
