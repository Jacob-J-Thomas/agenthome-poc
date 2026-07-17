using System.Globalization;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Application.Memory;
using EmbodySense.Core.Application.Runtime.State;
using EmbodySense.Core.Application.Runtime.Models;
using EmbodySense.Core.Common.Memory.Models;

namespace EmbodySense.Core.Application.Runtime.Commands;

public sealed class RuntimeCommandService
{
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

        if (!RuntimeCommandRegistry.TryMatch(input, out var command) || command.Id != RuntimeCommandId.Help)
        {
            result = RuntimeCommandResult.NotHandled;
            return false;
        }

        result = RuntimeCommandResult.HandledOutput(RuntimeCommandOutput.HelpText);
        return true;
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

        var commandText = RuntimeCommandRegistry.Normalize(input);
        if (_pendingConversationLoad is not null)
        {
            if (string.IsNullOrWhiteSpace(input) || RuntimeCommandRegistry.IsPendingInputCancellation(commandText))
            {
                ClearPendingInput();
                return RuntimeCommandResult.HandledOutput("Conversation load cancelled.");
            }

            if (!RuntimeCommandRegistry.IsKnown(commandText))
            {
                return await CompleteConversationLoadAsync(input, conversationState, cancellationToken);
            }

            ClearPendingInput();
        }

        if (TryHandleStaticCommand(input, out var staticResult))
        {
            return staticResult;
        }

        if (!RuntimeCommandRegistry.TryMatch(commandText, out var command))
        {
            return RuntimeCommandResult.NotHandled;
        }

        switch (command.Id)
        {
            case RuntimeCommandId.VerboseStatus:
                return RuntimeCommandResult.HandledOutput(state.Verbose ? "Verbose mode is on." : "Verbose mode is off.");

            case RuntimeCommandId.VerboseEnable:
                state.SetVerbose(true);
                ClearPendingInput();
                return RuntimeCommandResult.HandledOutput(RuntimeCommandOutput.VerboseEnabledText);

            case RuntimeCommandId.VerboseDisable:
                state.SetVerbose(false);
                ClearPendingInput();
                return RuntimeCommandResult.HandledOutput("Verbose mode disabled.");

            case RuntimeCommandId.Exit:
                state.RequestExit();
                ClearPendingInput();
                return RuntimeCommandResult.HandledExit();

            case RuntimeCommandId.NewSession:
                await HandleNewSessionAsync(conversationState, state, cancellationToken);
                return RuntimeCommandResult.HandledOutput("Started a new conversation.");

            case RuntimeCommandId.ConversationHistory:
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
        using (await conversationState.AcquireExclusiveAccessAsync(cancellationToken))
        {
            if (_conversationMemoryStore is not null)
            {
                await _conversationMemoryStore.StartFreshConversationAsync(cancellationToken);
            }

            conversationState.ReplaceMessages(_startupMessages, _startupMessages.Count);
        }

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
        if (string.IsNullOrWhiteSpace(answer) || RuntimeCommandRegistry.IsPendingInputCancellation(answer))
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
            IReadOnlyList<LlmMessage> conversationMessages;
            using (await conversationState.AcquireExclusiveAccessAsync(cancellationToken))
            {
                conversationMessages = await _conversationMemoryStore!.LoadConversationAsync(selectedConversation.ConversationId, cancellationToken);
                await _conversationMemoryStore.ResumeConversationAsync(selectedConversation.ConversationId, cancellationToken);
                conversationState.ReplaceMessages(
                    _startupMessages.Concat(conversationMessages).ToArray(),
                    _startupMessages.Count,
                    RuntimeContextSource.RestoredConversationHistory,
                    $"Restored from conversation history `{selectedConversation.ConversationId}` at the user's request.");
            }
            return RuntimeCommandResult.HandledOutput($"Loaded conversation `{selectedConversation.ConversationId}` ({conversationMessages.Count} messages).", conversationMessages);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException)
        {
            return RuntimeCommandResult.HandledOutput($"Could not load conversation: {exception.Message}");
        }
    }

}
