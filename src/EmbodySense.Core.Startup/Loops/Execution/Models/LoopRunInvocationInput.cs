namespace EmbodySense.Core.Startup.Loops.Execution.Models;

/// <summary>
/// Identifies an optimistic, idempotent invocation against an exact loop definition and conversation.
/// </summary>
/// <param name="LoopId">The loop identifier.</param>
/// <param name="ExpectedDefinitionVersion">The expected definition version.</param>
/// <param name="ExpectedDefinitionHash">The expected definition hash.</param>
/// <param name="OperationId">The operation identifier.</param>
/// <param name="InvocationPrompt">The invocation prompt.</param>
public sealed record LoopRunInvocationInput(
    string LoopId,
    int ExpectedDefinitionVersion,
    string ExpectedDefinitionHash,
    string OperationId,
    string? InvocationPrompt);
