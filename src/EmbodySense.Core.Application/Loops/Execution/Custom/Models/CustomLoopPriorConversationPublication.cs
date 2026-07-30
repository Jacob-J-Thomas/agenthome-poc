namespace EmbodySense.Core.Application.Loops.Execution.Custom.Models;

public sealed record CustomLoopPriorConversationPublication(
    string OperationId,
    string CanonicalOutput,
    string CanonicalOutputHash);
