using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Application.Loops.Sleep.Models;

/// <summary>Reports one wake or restart-reconciliation outcome.</summary>
/// <param name="Status">The closed operation status.</param>
/// <param name="Evidence">The exact durable wake evidence when one was authenticated.</param>
/// <param name="ContinuationInvoked">Whether this call invoked the exact idempotent continuation port.</param>
public sealed record GovernedLoopWakeResult(
    GovernedLoopWakeResultStatus Status,
    GovernedLoopWakeEvidence? Evidence = null,
    bool ContinuationInvoked = false);
