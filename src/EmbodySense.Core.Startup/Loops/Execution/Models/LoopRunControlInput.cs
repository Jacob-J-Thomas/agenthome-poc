namespace EmbodySense.Core.Startup.Loops.Execution;

public sealed record LoopRunControlInput(
    string RunId,
    int ExpectedLifecycleVersion,
    string OperationId);
