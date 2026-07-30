namespace EmbodySense.Core.Startup.Loops.Execution.Models;

/// <summary>
/// Identifies an optimistic, idempotent pause, cancel, or resume request.
/// </summary>
/// <param name="RunId">The run identifier.</param>
/// <param name="ExpectedLifecycleVersion">The expected lifecycle version.</param>
/// <param name="OperationId">The operation identifier.</param>
public sealed record LoopRunControlInput(
    string RunId,
    int ExpectedLifecycleVersion,
    string OperationId);
