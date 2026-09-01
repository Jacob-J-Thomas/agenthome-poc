using EmbodySense.Core.Application.Loops.Sleep;
using EmbodySense.Core.Common.Loops.Execution.Sleep;
using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;
using EmbodySense.Core.Startup.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Startup.Loops.Execution.Sleep;

/// <summary>Builds trusted current all-family repair readiness evidence without admitting any background work.</summary>
public sealed class GovernedLoopCoordinatorRepairDependencyProbe : IGovernedLoopCoordinatorRepairDependencyPort
{
    private readonly TimeProvider _timeProvider;
    private readonly IGovernedLoopLocalWorkReadinessProbe _work;

    /// <summary>Creates a coordinator repair readiness probe over the canonical composed local work runner.</summary>
    public GovernedLoopCoordinatorRepairDependencyProbe(IGovernedLoopLocalWorkReadinessProbe work, TimeProvider? timeProvider = null)
    {
        _work = work ?? throw new ArgumentNullException(nameof(work));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<GovernedLoopCoordinatorRepairReadiness?> ReadAsync(
        string workspaceId,
        string coordinatorId,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetUtcNow(out var evaluatedAtUtc))
        {
            return null;
        }

        var results = await Task.WhenAll(
            ProbeAsync(GovernedLoopLocalWorkFamily.Schedule, cancellationToken),
            ProbeAsync(GovernedLoopLocalWorkFamily.Trigger, cancellationToken),
            ProbeAsync(GovernedLoopLocalWorkFamily.Wake, cancellationToken),
            ProbeAsync(GovernedLoopLocalWorkFamily.HumanInput, cancellationToken),
            ProbeAsync(GovernedLoopLocalWorkFamily.HumanReview, cancellationToken)).ConfigureAwait(false);
        return GovernedLoopSleepContractHash.Apply(new GovernedLoopCoordinatorRepairReadiness(
            GovernedLoopCoordinatorRepairReadiness.CurrentSchemaVersion,
            workspaceId,
            coordinatorId,
            IsReady(results[0]),
            IsReady(results[1]),
            IsReady(results[2]),
            IsReady(results[3]),
            IsReady(results[4]),
            evaluatedAtUtc,
            string.Empty));
    }

    private async Task<GovernedLoopLocalWorkResult?> ProbeAsync(GovernedLoopLocalWorkFamily family, CancellationToken cancellationToken)
    {
        try
        {
            return await _work.ProbeReadinessAsync(family, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsReady(GovernedLoopLocalWorkResult? result)
        => result?.Status is GovernedLoopLocalWorkResultStatus.Completed or GovernedLoopLocalWorkResultStatus.Empty;

    private bool TryGetUtcNow(out DateTimeOffset value)
    {
        try
        {
            value = _timeProvider.GetUtcNow();
            return value != default && value.Offset == TimeSpan.Zero;
        }
        catch
        {
            value = default;
            return false;
        }
    }
}
