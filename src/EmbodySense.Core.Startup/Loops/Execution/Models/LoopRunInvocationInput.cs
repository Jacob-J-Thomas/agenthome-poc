namespace EmbodySense.Core.Startup.Loops.Execution.Models;

public sealed record LoopRunInvocationInput(
    string LoopId,
    int ExpectedDefinitionVersion,
    string ExpectedDefinitionHash,
    string OperationId,
    string? InvocationPrompt);
