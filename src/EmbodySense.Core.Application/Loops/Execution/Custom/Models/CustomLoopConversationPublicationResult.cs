namespace EmbodySense.Core.Application.Loops.Execution.Custom.Models;

public sealed record CustomLoopConversationPublicationResult(
    CustomLoopConversationPublicationOutcome Outcome,
    string? PublicationId,
    string Detail);
