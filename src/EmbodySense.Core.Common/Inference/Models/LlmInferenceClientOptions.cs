namespace EmbodySense.Core.Common.Inference.Models;

public sealed record LlmInferenceClientOptions
{
    public required LlmInferenceSurface Surface { get; init; }

    public string? Model { get; init; }

    public string? WorkingDirectory { get; init; }

    public string? CodexExecutablePath { get; init; }

    public string CodexSandbox { get; init; } = "read-only";
}
