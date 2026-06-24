using System.Globalization;
using System.Text;
using EmbodySense.Core.Application.Memory.Models;

namespace EmbodySense.Core.Application.Harness;

public static class HarnessCommandOutput
{
    private const int ConversationPromptPreviewLength = 96;

    public static IReadOnlyList<string> HelpLines { get; } =
    [
        "Harness commands:",
        "/help, /commands - list harness commands",
        "/new, /new-session - start a fresh conversation without leaving the harness",
        "/history, /conversations, /load - load a saved conversation before the first prompt in the current session",
        "/exit, /quit - leave the harness"
    ];

    public static string HelpText => string.Join(Environment.NewLine, HelpLines);

    public static string FormatConversationList(IReadOnlyList<ConversationTranscriptListItem> conversations)
    {
        ArgumentNullException.ThrowIfNull(conversations);

        var builder = new StringBuilder();
        builder.AppendLine("Stored conversations:");
        for (var i = 0; i < conversations.Count; i++)
        {
            builder.AppendLine(FormatConversationLine(i + 1, conversations[i]));
        }

        return builder.ToString().TrimEnd();
    }

    public static string FormatConversationLine(int number, ConversationTranscriptListItem conversation)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(number);
        ArgumentNullException.ThrowIfNull(conversation);

        var currentMarker = conversation.IsCurrent ? " (current)" : "";
        var timestamp = conversation.LastTimestampUtc.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
        var promptPreview = FormatPromptPreview(conversation.FirstPrompt);
        return $"{number}. {conversation.ConversationId}{currentMarker} | {conversation.MessageCount} messages | {timestamp} | {promptPreview}";
    }

    public static string FormatPromptPreview(string? prompt)
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
