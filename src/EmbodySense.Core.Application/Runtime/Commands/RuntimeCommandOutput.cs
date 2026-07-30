using System.Globalization;
using System.Text;
using EmbodySense.Core.Common.Memory.Models;

namespace EmbodySense.Core.Application.Runtime.Commands;

/// <summary>
/// Provides operations for runtime command output.
/// </summary>
public static class RuntimeCommandOutput
{
    private const int ConversationPromptPreviewLength = 96;

    /// <summary>
    /// Gets the help lines text values.
    /// </summary>
    /// <value>The help lines text values.</value>
    public static IReadOnlyList<string> HelpLines
    {
        get
        {
            var lines = new List<string> { "Runtime commands:" };
            lines.AddRange(RuntimeCommandRegistry.HelpCommands.Select(command => command.FormatHelpLine()));
            return lines;
        }
    }

    /// <summary>
    /// Gets the help text.
    /// </summary>
    /// <value>The help text.</value>
    public static string HelpText => string.Join(Environment.NewLine, HelpLines);

    /// <summary>
    /// Identifies the verbose enabled text runtime command output.
    /// </summary>
    public const string VerboseEnabledText = "Verbose mode enabled. EmbodySense will print visible inference context; this is not private model reasoning or hidden chain-of-thought.";

    /// <summary>
    /// Formats the conversation list.
    /// </summary>
    /// <param name="conversations">The conversations.</param>
    /// <returns>The text value.</returns>
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

    /// <summary>
    /// Formats the conversation line.
    /// </summary>
    /// <param name="number">The number.</param>
    /// <param name="conversation">The conversation.</param>
    /// <returns>The text value.</returns>
    public static string FormatConversationLine(int number, ConversationTranscriptListItem conversation)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(number);
        ArgumentNullException.ThrowIfNull(conversation);

        var currentMarker = conversation.IsCurrent ? " (current)" : "";
        var timestamp = conversation.LastTimestampUtc.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
        var promptPreview = FormatPromptPreview(conversation.FirstPrompt);
        return $"{number}. {conversation.ConversationId}{currentMarker} | {conversation.MessageCount} messages | {timestamp} | {promptPreview}";
    }

    /// <summary>
    /// Formats the prompt preview.
    /// </summary>
    /// <param name="prompt">The prompt.</param>
    /// <returns>The text value.</returns>
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
