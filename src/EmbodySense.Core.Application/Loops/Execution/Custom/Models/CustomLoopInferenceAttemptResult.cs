namespace EmbodySense.Core.Application.Loops.Execution.Custom.Models;

/// <summary>
/// Represents a custom loop inference attempt result.
/// </summary>
/// <param name="OutputText">The output text.</param>
/// <param name="Provider">The provider.</param>
/// <param name="Model">The model.</param>
/// <param name="ProviderResponseId">The provider response ID.</param>
/// <param name="ToolRequestsConsumed">The tool requests consumed.</param>
public sealed record CustomLoopInferenceAttemptResult(
    string OutputText,
    string Provider,
    string? Model,
    string? ProviderResponseId,
    int ToolRequestsConsumed = 0);
