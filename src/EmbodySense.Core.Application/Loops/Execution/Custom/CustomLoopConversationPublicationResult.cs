namespace EmbodySense.Core.Application.Loops.Execution.Custom;

public sealed record CustomLoopConversationPublicationResult(
    CustomLoopConversationPublicationOutcome Outcome,
    string? PublicationId,
    string Detail);
