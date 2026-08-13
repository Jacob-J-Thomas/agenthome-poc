using EmbodySense.Core.Startup.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Startup.Loops.Execution.Sleep;

/// <summary>Admits only wake work while the adjacent schedule and trigger families retain their explicit hosts.</summary>
public sealed class GovernedLoopWaitOnlyWorkRunner : IGovernedLoopLocalWorkRunner
{
    private static readonly GovernedLoopLocalWorkResult _familyNotOwned = new(
        GovernedLoopLocalWorkResultStatus.Empty,
        "family-not-owned");
    private readonly IGovernedLoopLocalWorkRunner _inner;

    /// <summary>Creates a Wake-only projection over the canonical one-shot work runner.</summary>
    public GovernedLoopWaitOnlyWorkRunner(IGovernedLoopLocalWorkRunner inner)
        => _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    /// <summary>Delegates Wake work and returns an explicit empty outcome for every adjacent family.</summary>
    public Task<GovernedLoopLocalWorkResult?> RunOnceAsync(
        GovernedLoopLocalWorkFamily family,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return family == GovernedLoopLocalWorkFamily.Wake
            ? _inner.RunOnceAsync(family, cancellationToken)
            : Task.FromResult<GovernedLoopLocalWorkResult?>(_familyNotOwned);
    }
}
