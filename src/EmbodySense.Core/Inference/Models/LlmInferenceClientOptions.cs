namespace EmbodySense.Core.Inference.Models;

public sealed record LlmInferenceClientOptions
{
    public required LlmInferenceSurface Surface { get; init; }

    public string? Model { get; init; }

    public string? WorkingDirectory { get; init; }

    public string? CodexExecutablePath { get; init; }

    public string CodexSandbox { get; init; } = "read-only";

    public string CodexApprovalPolicy { get; init; } = "never";

    public bool UseEphemeralCodexSession { get; init; } = true;

    public bool SkipCodexGitRepositoryCheck { get; init; }
}
