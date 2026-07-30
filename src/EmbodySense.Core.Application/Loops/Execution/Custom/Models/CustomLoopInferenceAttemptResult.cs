namespace EmbodySense.Core.Application.Loops.Execution.Custom.Models;

public sealed record CustomLoopInferenceAttemptResult(
    string OutputText,
    string Provider,
    string? Model,
    string? ProviderResponseId,
    int ToolRequestsConsumed = 0);
