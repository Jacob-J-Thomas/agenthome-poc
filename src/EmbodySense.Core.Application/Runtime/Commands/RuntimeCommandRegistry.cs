using EmbodySense.Core.Application.Runtime.Commands.Models;
namespace EmbodySense.Core.Application.Runtime.Commands;

/// <summary>
/// Provides operations for runtime command registry.
/// </summary>
public static class RuntimeCommandRegistry
{
    /// <summary>
    /// Gets the commands runtime command definitions.
    /// </summary>
    /// <value>The commands runtime command definitions.</value>
    public static IReadOnlyList<RuntimeCommandDefinition> Commands { get; } =
    [
        new RuntimeCommandDefinition(RuntimeCommandId.Help, ["/help", "/commands"], "list runtime commands"),
        new RuntimeCommandDefinition(
            RuntimeCommandId.VerboseStatus,
            ["/verbose"],
            "show or change visible-context debug output",
            ["/verbose", "/verbose on", "/verbose off"]),
        new RuntimeCommandDefinition(RuntimeCommandId.VerboseEnable, ["/verbose on", "/verbose true"], includeInHelp: false),
        new RuntimeCommandDefinition(RuntimeCommandId.VerboseDisable, ["/verbose off", "/verbose false"], includeInHelp: false),
        new RuntimeCommandDefinition(RuntimeCommandId.NewSession, ["/new", "/new-session"], "start a fresh conversation without leaving the session"),
        new RuntimeCommandDefinition(RuntimeCommandId.ConversationHistory, ["/history", "/conversations", "/load"], "load a saved conversation before the first prompt in the current session"),
        new RuntimeCommandDefinition(RuntimeCommandId.DefaultConversationReview, ["/review"], "inspect default-conversation review evidence; abandon outcome-unknown attempts only", ["/review", "/review resolve <turn-id>"]),
        new RuntimeCommandDefinition(RuntimeCommandId.HumanInput, ["/human-input"], "inspect or respond to canonical Human Input requests (CLI only)", ["/human-input help"]),
        new RuntimeCommandDefinition(RuntimeCommandId.Exit, ["exit", "quit", "/exit", "/quit"], "leave the session", ["/exit", "/quit"]),
        new RuntimeCommandDefinition(RuntimeCommandId.CancelPendingInput, ["/cancel", "cancel"], includeInHelp: false)
    ];

    /// <summary>
    /// Gets the help commands runtime command definitions.
    /// </summary>
    /// <value>The help commands runtime command definitions.</value>
    public static IReadOnlyList<RuntimeCommandDefinition> HelpCommands { get; } = Commands.Where(command => command.IncludeInHelp).ToArray();

    /// <summary>
    /// Attempts to match.
    /// </summary>
    /// <param name="input">The input.</param>
    /// <param name="definition">The definition.</param>
    /// <returns><see langword="true"/> when match; otherwise, <see langword="false"/>.</returns>
    public static bool TryMatch(string input, out RuntimeCommandDefinition definition)
    {
        var normalizedInput = Normalize(input);
        if (normalizedInput.Length == 0)
        {
            definition = null!;
            return false;
        }

        definition = Commands.FirstOrDefault(command => command.Matches(normalizedInput))!;
        return definition is not null;
    }

    /// <summary>
    /// Determines whether the input is known.
    /// </summary>
    /// <param name="input">The input.</param>
    /// <returns><see langword="true"/> when is known; otherwise, <see langword="false"/>.</returns>
    public static bool IsKnown(string input)
    {
        return TryMatch(input, out _);
    }

    /// <summary>
    /// Determines whether the input is pending input cancellation.
    /// </summary>
    /// <param name="input">The input.</param>
    /// <returns><see langword="true"/> when is pending input cancellation; otherwise, <see langword="false"/>.</returns>
    public static bool IsPendingInputCancellation(string input)
    {
        return TryMatch(input, out var definition) && definition.Id == RuntimeCommandId.CancelPendingInput;
    }

    /// <summary>
    /// Normalizes the requested value.
    /// </summary>
    /// <param name="input">The input.</param>
    /// <returns>The text value.</returns>
    public static string Normalize(string input)
    {
        return input.Trim().ToLowerInvariant();
    }
}
