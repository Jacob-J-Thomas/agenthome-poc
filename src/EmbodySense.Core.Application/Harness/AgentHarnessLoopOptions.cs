namespace EmbodySense.Core.Application.Harness;

public sealed record AgentHarnessLoopOptions
{
    public string Prompt { get; init; } = HarnessCommandOutput.UserPrompt;

    public string? Banner { get; init; }
}
