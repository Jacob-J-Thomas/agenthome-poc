namespace EmbodySense.Core.Application.Harness;

public sealed record HarnessCommandResult(
    bool Handled,
    string Output = "",
    string? Prompt = null,
    bool AwaitingInput = false,
    bool ExitRequested = false)
{
    public static HarnessCommandResult NotHandled { get; } = new(false);

    public static HarnessCommandResult HandledOutput(string output)
    {
        return new HarnessCommandResult(true, output);
    }

    public static HarnessCommandResult HandledPrompt(string output, string prompt)
    {
        return new HarnessCommandResult(true, output, prompt, AwaitingInput: true);
    }

    public static HarnessCommandResult HandledExit()
    {
        return new HarnessCommandResult(true, ExitRequested: true);
    }
}
