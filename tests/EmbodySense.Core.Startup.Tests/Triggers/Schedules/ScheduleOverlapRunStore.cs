using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Startup.Tests.Triggers.Schedules;

internal sealed class ScheduleOverlapRunStore(CustomLoopRunRecord? run = null, Exception? failure = null) : ICustomLoopRunStore, IDisposable
{
    internal int ReadCount { get; private set; }
    internal int DisposeCount { get; private set; }
    internal string? LastRequestedLoopId { get; private set; }

    public Task<CustomLoopRunRecord?> GetNonterminalByLoopAsync(string loopId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReadCount++;
        LastRequestedLoopId = loopId;
        if (failure is not null)
        {
            throw failure;
        }

        return Task.FromResult(run);
    }

    public Task<CustomLoopRunStoreResult> CreateAsync(CustomLoopRunRecord run, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<CustomLoopRunRecord?> GetAsync(string runId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<CustomLoopRunRecord?> GetByAdmissionOperationAsync(string admissionOperationId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<IReadOnlyList<CustomLoopRunSummary>> ListRecentAsync(int maximumCount, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<IReadOnlyList<CustomLoopRunRecord>> ListNonterminalAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<CustomLoopRunStoreResult> UpdateAsync(CustomLoopRunRecord run, int expectedLifecycleVersion, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public void Dispose() => DisposeCount++;
}
