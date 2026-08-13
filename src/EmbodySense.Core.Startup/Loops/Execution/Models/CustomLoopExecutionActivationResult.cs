namespace EmbodySense.Core.Startup.Loops.Execution.Models;

/// <summary>Reports whether an explicitly requested composition-owned execution dependency activated safely.</summary>
public sealed record CustomLoopExecutionActivationResult(
    bool Available,
    bool RetryAllowed,
    string Status,
    string Detail);
