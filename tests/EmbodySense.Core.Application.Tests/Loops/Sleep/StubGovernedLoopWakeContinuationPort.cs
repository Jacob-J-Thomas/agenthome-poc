using EmbodySense.Core.Application.Loops.Sleep;
using EmbodySense.Core.Application.Loops.Sleep.Models;

namespace EmbodySense.Core.Application.Tests.Loops.Sleep;

internal sealed class StubGovernedLoopWakeContinuationPort : IGovernedLoopWakeContinuationPort
{
    private readonly object _sync = new();
    private readonly HashSet<string> _committed = new(StringComparer.Ordinal);

    internal GovernedLoopWakeContinuationResult? ContinueResult { get; set; }

    internal GovernedLoopWakeContinuationResult? ReconcileResult { get; set; }

    internal Exception? ContinueException { get; set; }

    internal Exception? ReconcileException { get; set; }

    internal bool ReturnNullContinue { get; set; }

    internal bool ReturnNullReconcile { get; set; }

    internal bool ThrowAfterCommit { get; set; }

    internal Action<GovernedLoopWakeContinuationRequest, CancellationToken>? OnContinue { get; set; }

    internal int ContinueCount { get; private set; }

    internal int ReconcileCount { get; private set; }

    internal int CommittedOperationCount
    {
        get
        {
            lock (_sync)
            {
                return _committed.Count;
            }
        }
    }

    public Task<GovernedLoopWakeContinuationResult?> ContinueAsync(
        GovernedLoopWakeContinuationRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        OnContinue?.Invoke(request, cancellationToken);
        lock (_sync)
        {
            ContinueCount++;
            if (ContinueException is not null)
            {
                throw ContinueException;
            }

            if (ReturnNullContinue)
            {
                return Task.FromResult<GovernedLoopWakeContinuationResult?>(null);
            }

            var result = ContinueResult
                ?? new GovernedLoopWakeContinuationResult(
                    GovernedLoopWakeContinuationStatus.Committed,
                    GovernedLoopSleepApplicationTestFixture.Hash('6'));
            if (result.Status == GovernedLoopWakeContinuationStatus.Committed)
            {
                _committed.Add(request.ContinuationOperationId);
                if (ThrowAfterCommit)
                {
                    throw new InvalidOperationException("simulated crash after continuation commit");
                }
            }

            return Task.FromResult<GovernedLoopWakeContinuationResult?>(result);
        }
    }

    public Task<GovernedLoopWakeContinuationResult?> ReconcileAsync(
        GovernedLoopWakeContinuationRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ReconcileCount++;
            if (ReconcileException is not null)
            {
                throw ReconcileException;
            }

            if (ReturnNullReconcile)
            {
                return Task.FromResult<GovernedLoopWakeContinuationResult?>(null);
            }

            if (ReconcileResult is not null)
            {
                return Task.FromResult<GovernedLoopWakeContinuationResult?>(ReconcileResult);
            }

            return Task.FromResult<GovernedLoopWakeContinuationResult?>(
                _committed.Contains(request.ContinuationOperationId)
                    ? new GovernedLoopWakeContinuationResult(
                        GovernedLoopWakeContinuationStatus.Committed,
                        GovernedLoopSleepApplicationTestFixture.Hash('6'))
                    : new GovernedLoopWakeContinuationResult(
                        GovernedLoopWakeContinuationStatus.NotCommitted,
                        EvidenceReference: "continuation-not-found"));
        }
    }
}
