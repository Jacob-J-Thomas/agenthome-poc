namespace EmbodySense.Core.Startup.Loops.Execution.Models;

/// <summary>Projects the immutable run, revision, and execution-generation coordinates of a frontier.</summary>
public sealed record LoopRunFrontierBindingSnapshot(
    int SchemaVersion,
    string RunId,
    string GraphId,
    string RevisionId,
    string ExecutableHash,
    long ExecutionGeneration);
