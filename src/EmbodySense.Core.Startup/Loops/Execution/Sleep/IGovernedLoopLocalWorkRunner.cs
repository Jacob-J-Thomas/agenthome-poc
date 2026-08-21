using EmbodySense.Core.Startup.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Startup.Loops.Execution.Sleep;

/// <summary>Runs one subsystem-owned background operation at a safe one-shot boundary.</summary>
/// <remarks>
/// Implementations compose the canonical schedule evaluator, trigger worker, and sleep service. They must not report
/// completion before the underlying one-shot operation returns its durable outcome.
/// </remarks>
public interface IGovernedLoopLocalWorkRunner
{
    /// <summary>Attempts at most one item from the requested family.</summary>
    Task<GovernedLoopLocalWorkResult?> RunOnceAsync(
        GovernedLoopLocalWorkFamily family,
        CancellationToken cancellationToken = default);
}
