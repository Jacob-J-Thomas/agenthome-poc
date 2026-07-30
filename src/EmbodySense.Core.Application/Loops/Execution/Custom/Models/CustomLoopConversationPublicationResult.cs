namespace EmbodySense.Core.Application.Loops.Execution.Custom.Models;

/// <summary>
/// Represents a custom loop conversation publication result.
/// </summary>
/// <param name="Outcome">The outcome.</param>
/// <param name="PublicationId">The publication ID.</param>
/// <param name="Detail">The detail.</param>
public sealed record CustomLoopConversationPublicationResult(
    CustomLoopConversationPublicationOutcome Outcome,
    string? PublicationId,
    string Detail);
