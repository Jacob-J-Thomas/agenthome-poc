namespace EmbodySense.Core.Application.Runtime.Commands;

public static class RuntimeCommandRegistry
{
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
        new RuntimeCommandDefinition(RuntimeCommandId.Exit, ["exit", "quit", "/exit", "/quit"], "leave the session", ["/exit", "/quit"]),
        new RuntimeCommandDefinition(RuntimeCommandId.CancelPendingInput, ["/cancel", "cancel"], includeInHelp: false)
    ];

    public static IReadOnlyList<RuntimeCommandDefinition> HelpCommands { get; } = Commands.Where(command => command.IncludeInHelp).ToArray();

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

    public static bool IsKnown(string input)
    {
        return TryMatch(input, out _);
    }

    public static bool IsPendingInputCancellation(string input)
    {
        return TryMatch(input, out var definition) && definition.Id == RuntimeCommandId.CancelPendingInput;
    }

    public static string Normalize(string input)
    {
        return input.Trim().ToLowerInvariant();
    }
}
