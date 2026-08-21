using EmbodySense.Core.Application.Loops.Sleep;

namespace EmbodySense.Core.Startup.Loops.Execution.Sleep.Models;

/// <summary>Configures bounded candidate discovery and trigger-worker fairness for one local work runner.</summary>
/// <param name="TriggerWorkerId">The composition-owned process-local trigger worker identity.</param>
/// <param name="TriggerLeaseDuration">The bounded durable trigger ownership interval.</param>
/// <param name="MaximumConsecutiveTriggerSelectionsPerLoop">The largest admitted same-loop fairness suffix.</param>
/// <param name="CandidateReadLimit">The maximum candidates read from each durable background-work family.</param>
public sealed record GovernedLoopLocalWorkRunnerOptions(
    string TriggerWorkerId,
    TimeSpan TriggerLeaseDuration,
    int MaximumConsecutiveTriggerSelectionsPerLoop,
    int CandidateReadLimit)
{
    /// <summary>Gets the largest admitted per-family candidate discovery bound.</summary>
    public const int MaximumCandidateReadLimit = GovernedLoopBackgroundWorkContractLimits.MaxCandidatesPerFamily;
}
