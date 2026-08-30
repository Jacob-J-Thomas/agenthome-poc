using EmbodySense.Core.Common.Loops.Custom.Execution;

namespace EmbodySense.Core.Application.Loops.Execution.Custom.Models;

/// <summary>Returns the current durable run observation and safe terminalization posture for cancellation convergence.</summary>
/// <param name="Status">The closed reconciliation disposition.</param>
/// <param name="Run">The latest canonical run when it could be safely read.</param>
/// <param name="Detail">A bounded actionable explanation that contains no request response content.</param>
public sealed record CustomLoopHumanInputCancellationConvergenceResult(
    CustomLoopHumanInputCancellationConvergenceStatus Status,
    CustomLoopRunRecord? Run,
    string Detail);
