namespace EmbodySense.Core.Application.Loops.Models;

/// <summary>
/// Represents a custom loop execution lease result.
/// </summary>
/// <param name="Status">The status.</param>
/// <param name="Lease">The lease.</param>
/// <param name="Detail">The detail.</param>
public sealed record CustomLoopExecutionLeaseResult(CustomLoopExecutionLeaseStatus Status, ICustomLoopExecutionLease? Lease, string Detail);
