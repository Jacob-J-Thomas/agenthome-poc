using EmbodySense.Core.Application.Loops.Sequential.Models;

namespace EmbodySense.Core.Application.Loops.Sequential;

/// <summary>Freezes deterministic bounded scheduling policy without defining a second durable frontier contract.</summary>
public sealed class GovernedLoopTopologySchedulerPolicy
{
    private GovernedLoopTopologySchedulerPolicy(int maximumConcurrency)
    {
        MaximumConcurrency = maximumConcurrency;
    }

    /// <summary>The only supported POC running-node concurrency bound.</summary>
    public const int DefaultMaximumConcurrency = 1;
    /// <summary>Gets the concurrency-one claim bound; multiple nodes may remain Ready.</summary>
    public int MaximumConcurrency { get; }
    /// <summary>Gets the exact deterministic ready-node ordering.</summary>
    public GovernedLoopTopologyReadyOrdering ReadyOrdering => GovernedLoopTopologyReadyOrdering.StaticOrdinalThenNodeId;
    /// <summary>Gets whether two authority-bearing or otherwise effectful nodes may be claimed concurrently.</summary>
    public bool AllowsParallelEffectfulNodes => false;

    /// <summary>Creates the concurrency-one deterministic scheduling policy.</summary>
    /// <param name="maximumConcurrency">The requested concurrency ceiling, which must remain 1 at POC scope.</param>
    /// <returns>The immutable policy.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the concurrency ceiling is outside supported bounds.</exception>
    public static GovernedLoopTopologySchedulerPolicy Create(int maximumConcurrency = DefaultMaximumConcurrency)
    {
        if (maximumConcurrency != DefaultMaximumConcurrency)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumConcurrency));
        }

        return new GovernedLoopTopologySchedulerPolicy(maximumConcurrency);
    }
}
