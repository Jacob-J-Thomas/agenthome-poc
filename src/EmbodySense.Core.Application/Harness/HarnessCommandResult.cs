using EmbodySense.Core.Application.Inference.Models;

namespace EmbodySense.Core.Application.Harness;

public sealed record HarnessCommandResult
{
    public HarnessCommandResult(
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

    public static HarnessCommandResult NotHandled { get; } = new(false);

    public static HarnessCommandResult HandledOutput(string output, IReadOnlyList<LlmMessage>? restoredMessages = null)
    {
        return new HarnessCommandResult(true, output, restoredMessages: restoredMessages, replaceTranscript: restoredMessages is not null);
    }

    public static HarnessCommandResult HandledPrompt(string output, string prompt)
    {
        return new HarnessCommandResult(true, output, prompt, awaitingInput: true);
    }

    public static HarnessCommandResult HandledExit()
    {
        return new HarnessCommandResult(true, exitRequested: true);
    }
}
