using EmbodySense.Core.Startup.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Startup.Loops.Execution.Sleep;

/// <summary>Provides a non-actuating current dependency probe for one canonical local coordinator work family.</summary>
public interface IGovernedLoopLocalWorkReadinessProbe
{
    /// <summary>Inspects the family dependencies without claiming, dispatching, recovering, or changing durable work state.</summary>
    Task<GovernedLoopLocalWorkResult?> ProbeReadinessAsync(
        GovernedLoopLocalWorkFamily family,
        CancellationToken cancellationToken = default);
}
