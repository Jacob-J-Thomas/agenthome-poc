namespace EmbodySense.Core.Startup.Loops.Execution.Models;

public sealed record LoopRunControlInput(
    string RunId,
    int ExpectedLifecycleVersion,
    string OperationId);
