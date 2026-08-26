using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Persistence.Loops;

namespace EmbodySense.Core.Startup.Tests.Triggers.Schedules;

internal sealed class TrackingCustomLoopRunStore : ICustomLoopRunStore, IDisposable
{
    private readonly CustomLoopRunStore _inner;
    private bool _disposed;

    internal TrackingCustomLoopRunStore(CustomLoopRunStore inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    internal int CreateCallCount { get; private set; }
    internal string? LastCreatedRunId { get; private set; }
    internal int GetNonterminalByLoopCallCount { get; private set; }
    internal string? LastNonterminalLoopId { get; private set; }
    internal int DisposeCount { get; private set; }
    internal int InnerDisposeCount { get; private set; }
    internal bool IsDisposed => _disposed;

    public Task<CustomLoopRunStoreResult> CreateAsync(CustomLoopRunRecord run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        cancellationToken.ThrowIfCancellationRequested();
        CreateCallCount++;
        LastCreatedRunId = run.Id;
        return _inner.CreateAsync(run, cancellationToken);
    }

    public Task<CustomLoopRunRecord?> GetAsync(string runId, CancellationToken cancellationToken = default)
        => _inner.GetAsync(runId, cancellationToken);

    public Task<CustomLoopRunRecord?> GetByAdmissionOperationAsync(string admissionOperationId, CancellationToken cancellationToken = default)
        => _inner.GetByAdmissionOperationAsync(admissionOperationId, cancellationToken);

    public Task<CustomLoopRunRecord?> GetNonterminalByLoopAsync(string loopId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GetNonterminalByLoopCallCount++;
        LastNonterminalLoopId = loopId;
        return _inner.GetNonterminalByLoopAsync(loopId, cancellationToken);
    }

    public Task<IReadOnlyList<CustomLoopRunSummary>> ListRecentAsync(int maximumCount, CancellationToken cancellationToken = default)
        => _inner.ListRecentAsync(maximumCount, cancellationToken);

    public Task<IReadOnlyList<CustomLoopRunRecord>> ListNonterminalAsync(CancellationToken cancellationToken = default)
        => _inner.ListNonterminalAsync(cancellationToken);

    public Task<CustomLoopRunStoreResult> UpdateAsync(CustomLoopRunRecord run, int expectedLifecycleVersion, CancellationToken cancellationToken = default)
        => _inner.UpdateAsync(run, expectedLifecycleVersion, cancellationToken);

    public void Dispose()
    {
        DisposeCount++;
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        InnerDisposeCount++;
        _inner.Dispose();
    }
}
