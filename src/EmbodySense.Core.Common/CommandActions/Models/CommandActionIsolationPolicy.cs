namespace EmbodySense.Core.Common.CommandActions.Models;

/// <summary>Declares controls that must all be effective before child code executes.</summary>
/// <param name="WorkingDirectory">The exact working-directory class.</param>
/// <param name="Network">The network posture.</param>
/// <param name="MaxExecutionMilliseconds">The execution timeout.</param>
/// <param name="MaxTerminationMilliseconds">The separate process-tree termination wait.</param>
/// <param name="MaxMemoryBytes">The memory ceiling.</param>
/// <param name="MaxOutputBytes">The combined standard-stream byte ceiling.</param>
/// <param name="MaxConcurrency">The concurrency ceiling.</param>
/// <param name="RequireProcessTreeTermination">Whether termination must cover the complete process tree.</param>
public sealed record CommandActionIsolationPolicy(
    CommandActionWorkingDirectoryKind WorkingDirectory,
    CommandActionNetworkPolicy Network,
    int MaxExecutionMilliseconds,
    int MaxTerminationMilliseconds,
    long MaxMemoryBytes,
    int MaxOutputBytes,
    int MaxConcurrency,
    bool RequireProcessTreeTermination);
