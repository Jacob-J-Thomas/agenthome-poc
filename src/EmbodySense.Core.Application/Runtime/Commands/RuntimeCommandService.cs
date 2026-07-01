using System.Globalization;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Application.Memory;
using EmbodySense.Core.Application.Runtime.State;
using EmbodySense.Core.Application.Runtime.Models;
using EmbodySense.Core.Common.Memory.Models;

namespace EmbodySense.Core.Application.Runtime.Commands;

public sealed class RuntimeCommandService
{
    // TODO(runtime-command-registry): Runtime command names and aliases are still hard-coded in this service.
    // Revisit when commands need a typed registry shared by help text, pending-input handling, tests, and future agent-facing
    // runtime controls without moving command authority into individual UI clients.
    private readonly IConversationMemoryStore? _conversationMemoryStore;
    private readonly IReadOnlyList<LlmMessage> _startupMessages;
    private IReadOnlyList<ConversationTranscriptListItem>? _pendingConversationLoad;

    public RuntimeCommandService(
        IConversationMemoryStore? conversationMemoryStore = null,
        IReadOnlyList<LlmMessage>? startupMessages = null)
    {
        _conversationMemoryStore = conversationMemoryStore;
        _startupMessages = startupMessages ?? [];
    }

    public static bool TryHandleStaticCommand(string input, out RuntimeCommandResult result)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            result = RuntimeCommandResult.NotHandled;
            return false;
        }

        result = NormalizeCommand(input) switch
        {
            "/help" or "/commands" => RuntimeCommandResult.HandledOutput(RuntimeCommandOutput.HelpText),
            _ => RuntimeCommandResult.NotHandled
        };
        return result.Handled;
    }

    public async Task<RuntimeCommandResult> TryHandleAsync(
        string input,
        ConversationRuntimeState conversationState,
        RuntimeSessionState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(conversationState);
        ArgumentNullException.ThrowIfNull(state);

        var command = NormalizeCommand(input);
        if (_pendingConversationLoad is not null)
        {
            if (string.IsNullOrWhiteSpace(input) || IsPendingConversationSelectionCommand(command))
            {
                ClearPendingInput();
                return RuntimeCommandResult.HandledOutput("Conversation load cancelled.");
            }

            if (!IsKnownRuntimeCommand(command))
            {
                return await CompleteConversationLoadAsync(input, conversationState, cancellationToken);
            }

            ClearPendingInput();
        }

        if (TryHandleStaticCommand(input, out var staticResult))
        {
            return staticResult;
        }

        switch (command)
        {
            case "/verbose":
                return RuntimeCommandResult.HandledOutput(state.Verbose ? "Verbose mode is on." : "Verbose mode is off.");

            case "/verbose on" or "/verbose true":
                state.SetVerbose(true);
                ClearPendingInput();
                return RuntimeCommandResult.HandledOutput(RuntimeCommandOutput.VerboseEnabledText);

            case "/verbose off" or "/verbose false":
                state.SetVerbose(false);
                ClearPendingInput();
                return RuntimeCommandResult.HandledOutput("Verbose mode disabled.");

            case "exit" or "quit" or "/exit" or "/quit":
                state.RequestExit();
                ClearPendingInput();
                return RuntimeCommandResult.HandledExit();

            case "/new" or "/new-session":
                await HandleNewSessionAsync(conversationState, state, cancellationToken);
                return RuntimeCommandResult.HandledOutput("Started a new conversation.");

            case "/history" or "/conversations" or "/load":
                return await BeginConversationLoadAsync(state, cancellationToken);

            default:
                return RuntimeCommandResult.NotHandled;
        }
    }

    public void ClearPendingInput()
    {
        _pendingConversationLoad = null;
    }

    private async Task HandleNewSessionAsync(
        ConversationRuntimeState conversationState,
        RuntimeSessionState state,
        CancellationToken cancellationToken)
    {
        if (_conversationMemoryStore is not null)
        {
            await _conversationMemoryStore.StartFreshConversationAsync(cancellationToken);
        }

        conversationState.ReplaceMessages(_startupMessages, _startupMessages.Count);
        state.ResetModelTurn();
        ClearPendingInput();
    }

    private async Task<RuntimeCommandResult> BeginConversationLoadAsync(
        RuntimeSessionState state,
        CancellationToken cancellationToken)
    {
        if (state.ModelTurnStarted)
        {
            return RuntimeCommandResult.HandledOutput("Load a stored conversation before sending the first prompt in this session. Use /new first to start a fresh session.");
        }

        if (_conversationMemoryStore is null)
        {
            return RuntimeCommandResult.HandledOutput("Conversation history is not available for this session.");
        }

        var conversations = await _conversationMemoryStore.ListConversationsAsync(cancellationToken);
        if (conversations.Count == 0)
        {
            return RuntimeCommandResult.HandledOutput("No stored conversations were found.");
        }

        _pendingConversationLoad = conversations;
        const string prompt = "Send conversation number to load, or /cancel.";
        return RuntimeCommandResult.HandledPrompt(RuntimeCommandOutput.FormatConversationList(conversations), prompt);
    }

    private async Task<RuntimeCommandResult> CompleteConversationLoadAsync(
        string input,
        ConversationRuntimeState conversationState,
        CancellationToken cancellationToken)
    {
        var conversations = _pendingConversationLoad ?? [];
        ClearPendingInput();
        var answer = input.Trim();
        if (string.IsNullOrWhiteSpace(answer) || IsPendingConversationSelectionCommand(answer))
        {
            return RuntimeCommandResult.HandledOutput("Conversation load cancelled.");
        }

        if (!int.TryParse(answer, NumberStyles.Integer, CultureInfo.InvariantCulture, out var selectedNumber) || selectedNumber < 1 || selectedNumber > conversations.Count)
        {
            return RuntimeCommandResult.HandledOutput("Invalid conversation selection.");
        }

        var selectedConversation = conversations[selectedNumber - 1];
        try
        {
            var conversationMessages = await _conversationMemoryStore!.LoadConversationAsync(selectedConversation.ConversationId, cancellationToken);
            await _conversationMemoryStore.ResumeConversationAsync(selectedConversation.ConversationId, cancellationToken);
            conversationState.ReplaceMessages(
                _startupMessages.Concat(conversationMessages).ToArray(),
                _startupMessages.Count,
                RuntimeContextSource.RestoredConversationHistory,
                $"Restored from conversation history `{selectedConversation.ConversationId}` at the user's request.");
            return RuntimeCommandResult.HandledOutput($"Loaded conversation `{selectedConversation.ConversationId}` ({conversationMessages.Count} messages).", conversationMessages);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException)
        {
            return RuntimeCommandResult.HandledOutput($"Could not load conversation: {exception.Message}");
        }
    }

    private static string NormalizeCommand(string input)
    {
        return input.Trim().ToLowerInvariant();
    }

    private static bool IsKnownRuntimeCommand(string command)
    {
        return command is "exit" or "quit" or "/exit" or "/quit" or "/help" or "/commands" or "/verbose" or "/verbose on" or "/verbose true" or "/verbose off" or "/verbose false" or "/new" or "/new-session" or "/history" or "/conversations" or "/load";
    }

    private static bool IsPendingConversationSelectionCommand(string command)
    {
        return string.Equals(command, "/cancel", StringComparison.OrdinalIgnoreCase) || string.Equals(command, "cancel", StringComparison.OrdinalIgnoreCase);
    }
}
