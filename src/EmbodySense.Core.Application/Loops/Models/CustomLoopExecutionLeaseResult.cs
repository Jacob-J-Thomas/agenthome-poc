namespace EmbodySense.Core.Application.Loops.Models;

public sealed record CustomLoopExecutionLeaseResult(CustomLoopExecutionLeaseStatus Status, ICustomLoopExecutionLease? Lease, string Detail);
