namespace EmbodySense.Core.Application.Loops.Execution.Custom;

public sealed record CustomLoopConversationPublicationRequest(
    string OperationId,
    string RunId,
    string LoopId,
    int Iteration,
    string StepId,
    string ConversationId,
    string ExpectedConversationVersion,
    string CanonicalOutput,
    string CanonicalOutputHash,
    IReadOnlyList<CustomLoopPriorConversationPublication>? PriorPublications = null);
