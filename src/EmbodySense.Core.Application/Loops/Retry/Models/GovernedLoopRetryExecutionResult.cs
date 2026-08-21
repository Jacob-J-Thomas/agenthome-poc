using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution.Retry.Models;

namespace EmbodySense.Core.Application.Loops.Retry.Models;

/// <summary>Returns the durable run and retry state produced by one schedule attempt.</summary>
/// <param name="Status">The closed scheduling status.</param>
/// <param name="Run">The latest exact durable run when available.</param>
/// <param name="State">The latest exact retry state when available.</param>
/// <param name="Detail">A bounded value-free outcome reason.</param>
public sealed record GovernedLoopRetryExecutionResult(
    GovernedLoopRetryExecutionStatus Status,
    CustomLoopRunRecord? Run,
    GovernedLoopRetryState? State,
    string Detail);
