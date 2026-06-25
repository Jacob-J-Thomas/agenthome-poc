using System.Globalization;
using EmbodySense.Core.Application.Inference.Models;
using EmbodySense.Core.Application.Memory;
using EmbodySense.Core.Application.Memory.Models;

namespace EmbodySense.Core.Application.Harness;

public sealed class HarnessCommandService
{
    private readonly IConversationMemoryStore? _conversationMemoryStore;
    private readonly IReadOnlyList<LlmMessage> _startupMessages;
    private IReadOnlyList<ConversationTranscriptListItem>? _pendingConversationLoad;

    public HarnessCommandService(
        IConversationMemoryStore? conversationMemoryStore = null,
        IReadOnlyList<LlmMessage>? startupMessages = null)
    {
        _conversationMemoryStore = conversationMemoryStore;
        _startupMessages = startupMessages ?? [];
    }

    public static bool TryHandleStaticCommand(string input, out HarnessCommandResult result)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            result = HarnessCommandResult.NotHandled;
            return false;
        }

        result = NormalizeCommand(input) switch
        {
            "/help" or "/commands" => HarnessCommandResult.HandledOutput(HarnessCommandOutput.HelpText),
            _ => HarnessCommandResult.NotHandled
        };
        return result.Handled;
    }

    public async Task<HarnessCommandResult> TryHandleAsync(
        string input,
        AgentHarnessSession session,
        HarnessLoopState state,
        HarnessCommandInteractionMode interactionMode = HarnessCommandInteractionMode.InlineSelection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(state);

        var command = NormalizeCommand(input);
        if (_pendingConversationLoad is not null)
        {
            if (interactionMode == HarnessCommandInteractionMode.DeferredSelection && IsKnownHarnessCommand(command) && !IsPendingConversationSelectionCommand(command))
            {
                _pendingConversationLoad = null;
            }
            else
            {
                return await CompleteConversationLoadAsync(input, session, interactionMode, cancellationToken);
            }
        }

        if (TryHandleStaticCommand(input, out var staticResult))
        {
            return staticResult;
        }

        switch (command)
        {
            case "/verbose":
                return HarnessCommandResult.HandledOutput(state.Verbose ? "Verbose mode is on." : "Verbose mode is off.");

            case "/verbose on" or "/verbose true":
                state.SetVerbose(true);
                ClearPendingInput();
                return HarnessCommandResult.HandledOutput(HarnessCommandOutput.VerboseEnabledText);

            case "/verbose off" or "/verbose false":
                state.SetVerbose(false);
                ClearPendingInput();
                return HarnessCommandResult.HandledOutput("Verbose mode disabled.");

            case "exit" or "quit" or "/exit" or "/quit":
                state.RequestExit();
                ClearPendingInput();
                return HarnessCommandResult.HandledExit();

            case "/new" or "/new-session":
                await HandleNewSessionAsync(session, state, cancellationToken);
                return HarnessCommandResult.HandledOutput("Started a new conversation.");

            case "/history" or "/conversations" or "/load":
                return await BeginConversationLoadAsync(state, interactionMode, cancellationToken);

            default:
                return HarnessCommandResult.NotHandled;
        }
    }

    public void ClearPendingInput()
    {
        _pendingConversationLoad = null;
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
        ClearPendingInput();
    }

    private async Task<HarnessCommandResult> BeginConversationLoadAsync(
        HarnessLoopState state,
        HarnessCommandInteractionMode interactionMode,
        CancellationToken cancellationToken)
    {
        if (state.ModelTurnStarted)
        {
            return HarnessCommandResult.HandledOutput("Load a stored conversation before sending the first prompt in this session. Use /new first to start a fresh session.");
        }

        if (_conversationMemoryStore is null)
        {
            return HarnessCommandResult.HandledOutput("Conversation history is not available for this session.");
        }

        var conversations = await _conversationMemoryStore.ListConversationsAsync(cancellationToken);
        if (conversations.Count == 0)
        {
            return HarnessCommandResult.HandledOutput("No stored conversations were found.");
        }

        _pendingConversationLoad = conversations;
        var prompt = interactionMode == HarnessCommandInteractionMode.DeferredSelection
            ? "Send conversation number to load, or /cancel."
            : "Select conversation number to load, or press Enter to cancel: ";
        return HarnessCommandResult.HandledPrompt(HarnessCommandOutput.FormatConversationList(conversations), prompt);
    }

    private async Task<HarnessCommandResult> CompleteConversationLoadAsync(
        string input,
        AgentHarnessSession session,
        HarnessCommandInteractionMode interactionMode,
        CancellationToken cancellationToken)
    {
        var conversations = _pendingConversationLoad ?? [];
        ClearPendingInput();
        var answer = input.Trim();
        if (string.IsNullOrWhiteSpace(answer) || interactionMode == HarnessCommandInteractionMode.DeferredSelection && IsPendingConversationSelectionCommand(answer))
        {
            return HarnessCommandResult.HandledOutput("Conversation load cancelled.");
        }

        if (!int.TryParse(answer, NumberStyles.Integer, CultureInfo.InvariantCulture, out var selectedNumber) || selectedNumber < 1 || selectedNumber > conversations.Count)
        {
            return HarnessCommandResult.HandledOutput("Invalid conversation selection.");
        }

        var selectedConversation = conversations[selectedNumber - 1];
        try
        {
            var conversationMessages = await _conversationMemoryStore!.LoadConversationAsync(selectedConversation.ConversationId, cancellationToken);
            await _conversationMemoryStore.ResumeConversationAsync(selectedConversation.ConversationId, cancellationToken);
            session.ReplaceMessages(_startupMessages.Concat(conversationMessages).ToArray());
            return HarnessCommandResult.HandledOutput($"Loaded conversation `{selectedConversation.ConversationId}` ({conversationMessages.Count} messages).", conversationMessages);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException)
        {
            return HarnessCommandResult.HandledOutput($"Could not load conversation: {exception.Message}");
        }
    }

    private static string NormalizeCommand(string input)
    {
        return input.Trim().ToLowerInvariant();
    }

    private static bool IsKnownHarnessCommand(string command)
    {
        return command is "exit" or "quit" or "/exit" or "/quit" or "/help" or "/commands" or "/verbose" or "/verbose on" or "/verbose true" or "/verbose off" or "/verbose false" or "/new" or "/new-session" or "/history" or "/conversations" or "/load";
    }

    private static bool IsPendingConversationSelectionCommand(string command)
    {
        return string.Equals(command, "/cancel", StringComparison.OrdinalIgnoreCase) || string.Equals(command, "cancel", StringComparison.OrdinalIgnoreCase);
    }
}
