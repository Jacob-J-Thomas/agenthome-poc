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
            "/history" or "/conversations" or "/load" => await HandleConversationLoadAsync(session, modelTurnStarted, cancellationToken),
            _ => HarnessCommandResult.NotHandled
        };
    }

    private async Task<HarnessCommandResult> HandleConversationLoadAsync(
        AgentHarnessSession session,
        bool modelTurnStarted,
        CancellationToken cancellationToken)
    {
        if (modelTurnStarted)
        {
            Console.WriteLine("Load a stored conversation before sending the first prompt in this run. Exit and run again to load a different transcript.");
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
