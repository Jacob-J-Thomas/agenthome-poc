namespace EmbodySense.Core.Application.Loops.Execution.Custom;

public sealed record CustomLoopPriorConversationPublication(
    string OperationId,
    string CanonicalOutput,
    string CanonicalOutputHash);
