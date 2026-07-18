namespace EmbodySense.Core.Application.Loops;

public sealed record CustomLoopExecutionLeaseResult(CustomLoopExecutionLeaseStatus Status, ICustomLoopExecutionLease? Lease, string Detail);
