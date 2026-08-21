namespace EmbodySense.Core.Application.Loops.Execution.Custom.Models;

using EmbodySense.Core.Common.Inference.Profiles.Models;

/// <summary>
/// Represents a custom loop inference attempt result.
/// </summary>
/// <param name="OutputText">The output text.</param>
/// <param name="Provider">The provider.</param>
/// <param name="Model">The model.</param>
/// <param name="ProviderResponseId">The provider response ID.</param>
/// <param name="ToolRequestsConsumed">The tool requests consumed.</param>
/// <param name="ModelExecutionEvidence">The exact canonical profile, reservation, and reconciled usage evidence; null only for the legacy compatibility path.</param>
public sealed record CustomLoopInferenceAttemptResult(
    string OutputText,
    string Provider,
    string? Model,
    string? ProviderResponseId,
    int ToolRequestsConsumed = 0,
    GovernedModelAttemptExecutionEvidence? ModelExecutionEvidence = null);
