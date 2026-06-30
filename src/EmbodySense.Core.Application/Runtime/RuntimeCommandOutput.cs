using System.Globalization;
using System.Text;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Common.Memory.Models;

namespace EmbodySense.Core.Application.Runtime;

public static class RuntimeCommandOutput
{
    // TODO(runtime-command-output): Split command constants from transcript/verbose formatters if this grows beyond the current small shared command surface.
    // Deferred because Web and CLI intentionally share the same text today; revisit when runtime events carry structured payloads instead of formatted strings.
    private const int ConversationPromptPreviewLength = 96;
    public const string UserPrompt = "User: ";

    public static IReadOnlyList<string> HelpLines { get; } =
    [
        "Runtime commands:",
        "/help, /commands - list runtime commands",
        "/verbose, /verbose on, /verbose off - show or change visible-context debug output",
        "/new, /new-session - start a fresh conversation without leaving the session",
        "/history, /conversations, /load - load a saved conversation before the first prompt in the current session",
        "/exit, /quit - leave the session"
    ];

    public static string HelpText => string.Join(Environment.NewLine, HelpLines);

    public const string VerboseEnabledText = "Verbose mode enabled. EmbodySense will print visible inference context; this is not private model reasoning or hidden chain-of-thought.";

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

    public static string FormatRestoredConversation(IReadOnlyList<LlmMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        if (messages.Count == 0)
        {
            return "Loaded conversation transcript is empty.";
        }

        var builder = new StringBuilder();
        builder.AppendLine("Loaded conversation transcript:");
        foreach (var message in messages)
        {
            builder.AppendLine(FormatMessageHeader(message.Role));
            builder.AppendLine(message.Content);
            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    public static string FormatVerboseContext(IReadOnlyList<LlmMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var builder = new StringBuilder();
        builder.AppendLine("[verbose] Visible inference context follows.");
        builder.AppendLine("[verbose] This is the startup, restored, and session context EmbodySense is sending for the next model turn.");
        builder.AppendLine("[verbose] This is not private model reasoning, hidden chain-of-thought, or provider-internal state.");
        foreach (var message in messages)
        {
            builder.AppendLine(FormatMessageHeader(message.Role));
            builder.AppendLine(message.Content);
            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    public static string FormatMessageHeader(LlmMessageRole role)
    {
        return role switch
        {
            LlmMessageRole.System => "System:",
            LlmMessageRole.User => "User:",
            LlmMessageRole.Assistant => "Assistant:",
            LlmMessageRole.Tool => "Tool:",
            _ => role.ToString()
        };
    }
}
