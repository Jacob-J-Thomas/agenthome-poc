namespace EmbodySense.Core.Startup.Loops.Execution.Models;

/// <summary>Projects the bounded run fields safe to return across an untrusted interface transport.</summary>
/// <param name="Id">The server-owned durable run identity.</param>
/// <param name="Status">The current durable run status.</param>
/// <param name="FinalOutput">The bounded terminal output, when one is durable.</param>
/// <param name="FailureCode">The bounded terminal failure code, when present.</param>
/// <param name="FailureDetail">The bounded terminal failure detail, when present.</param>
public sealed record GovernedLoopRunTransportSnapshot(
    string Id,
    string Status,
    string? FinalOutput,
    string? FailureCode,
    string? FailureDetail);
