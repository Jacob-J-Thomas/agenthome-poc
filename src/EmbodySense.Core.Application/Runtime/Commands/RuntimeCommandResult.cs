using EmbodySense.Core.Common.Inference.Models;

namespace EmbodySense.Core.Application.Runtime.Commands;

public sealed record RuntimeCommandResult
{
    public RuntimeCommandResult(
        bool handled,
        string output = "",
        string? prompt = null,
        bool awaitingInput = false,
        bool exitRequested = false,
        IReadOnlyList<LlmMessage>? restoredMessages = null,
        bool replaceTranscript = false)
    {
        Handled = handled;
        Output = output;
        Prompt = prompt;
        AwaitingInput = awaitingInput;
        ExitRequested = exitRequested;
        RestoredMessages = restoredMessages ?? [];
        ReplaceTranscript = replaceTranscript;
    }

    public bool Handled { get; }

    public string Output { get; }

    public string? Prompt { get; }

    public bool AwaitingInput { get; }

    public bool ExitRequested { get; }

    public IReadOnlyList<LlmMessage> RestoredMessages { get; }

    public bool ReplaceTranscript { get; }

    public static RuntimeCommandResult NotHandled { get; } = new(false);

    public static RuntimeCommandResult HandledOutput(string output, IReadOnlyList<LlmMessage>? restoredMessages = null)
    {
        return new RuntimeCommandResult(true, output, restoredMessages: restoredMessages, replaceTranscript: restoredMessages is not null);
    }

    public static RuntimeCommandResult HandledPrompt(string output, string prompt)
    {
        return new RuntimeCommandResult(true, output, prompt, awaitingInput: true);
    }

    public static RuntimeCommandResult HandledExit()
    {
        return new RuntimeCommandResult(true, exitRequested: true);
    }
}
