using EmbodySense.Core.Application.Loops.Wait;
using EmbodySense.Core.Startup.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Startup.Loops.Execution.Sleep;

/// <summary>Runs one bounded retained-Wait recovery sweep before ordinary wake discovery.</summary>
public sealed class GovernedLoopRecoveringWaitWorkRunner : IGovernedLoopLocalWorkRunner
{
    private readonly IGovernedLoopLocalWorkRunner _inner;
    private readonly int _maximumRecoveryCount;
    private readonly IGovernedLoopWaitRecoveryPort _recovery;

    /// <summary>Creates one recovery-first projection over the canonical local work runner.</summary>
    /// <param name="inner">The canonical local work runner used after retained Wait recovery is idle.</param>
    /// <param name="recovery">The canonical retained Wait recovery boundary.</param>
    /// <param name="maximumRecoveryCount">The bounded recovery page size from 1 through 256.</param>
    public GovernedLoopRecoveringWaitWorkRunner(
        IGovernedLoopLocalWorkRunner inner,
        IGovernedLoopWaitRecoveryPort recovery,
        int maximumRecoveryCount)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _recovery = recovery ?? throw new ArgumentNullException(nameof(recovery));
        _maximumRecoveryCount = maximumRecoveryCount is < 1 or > 256
            ? throw new ArgumentOutOfRangeException(nameof(maximumRecoveryCount))
            : maximumRecoveryCount;
    }

    /// <inheritdoc />
    public async Task<GovernedLoopLocalWorkResult?> RunOnceAsync(
        GovernedLoopLocalWorkFamily family,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (family != GovernedLoopLocalWorkFamily.Wake)
        {
            return await _inner.RunOnceAsync(family, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var recovery = await _recovery.RecoverAsync(_maximumRecoveryCount, cancellationToken).ConfigureAwait(false);
            if (recovery.NeedsReview > 0)
            {
                return new GovernedLoopLocalWorkResult(
                    GovernedLoopLocalWorkResultStatus.AttentionRequired,
                    "wait-recovery-needs-review");
            }

            if (recovery.Recovered > 0)
            {
                return new GovernedLoopLocalWorkResult(
                    GovernedLoopLocalWorkResultStatus.Completed,
                    "wait-recovery-completed");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new GovernedLoopLocalWorkResult(
                GovernedLoopLocalWorkResultStatus.Unavailable,
                "wait-recovery-unavailable");
        }

        return await _inner.RunOnceAsync(family, cancellationToken).ConfigureAwait(false);
    }
}
