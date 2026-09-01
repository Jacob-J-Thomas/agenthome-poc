using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution.Sleep;

internal sealed class HumanReviewRecoveryRecordingRunStore : ICustomLoopRunStore
{
    public Func<CustomLoopRunPageRequest, CustomLoopRunPage>? PageFactory { get; set; }
    public Func<string, CustomLoopRunRecord?>? GetFactory { get; set; }
    public List<string?> Cursors { get; } = [];
    public int GetCalls { get; private set; }
    public int MaxConcurrent { get; private set; }
    private int _active;

    public Task<CustomLoopRunStoreResult> CreateAsync(CustomLoopRunRecord run, CancellationToken cancellationToken = default) => Task.FromResult(CustomLoopRunStoreResult.NotFound());
    public Task<CustomLoopRunRecord?> GetAsync(string runId, CancellationToken cancellationToken = default)
    {
        GetCalls++;
        return Task.FromResult(GetFactory?.Invoke(runId));
    }
    public Task<CustomLoopRunRecord?> GetByAdmissionOperationAsync(string admissionOperationId, CancellationToken cancellationToken = default) => Task.FromResult<CustomLoopRunRecord?>(null);
    public Task<CustomLoopRunRecord?> GetNonterminalByLoopAsync(string loopId, CancellationToken cancellationToken = default) => Task.FromResult<CustomLoopRunRecord?>(null);
    public Task<IReadOnlyList<CustomLoopRunSummary>> ListRecentAsync(int maximumCount, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CustomLoopRunSummary>>([]);
    public async Task<CustomLoopRunPage> ListPageAsync(CustomLoopRunPageRequest request, CancellationToken cancellationToken = default)
    {
        Cursors.Add(request.Cursor);
        var active = Interlocked.Increment(ref _active);
        MaxConcurrent = Math.Max(MaxConcurrent, active);
        try
        {
            await Task.Delay(5, cancellationToken);
            return PageFactory?.Invoke(request) ?? new CustomLoopRunPage([], null);
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }
    public Task<IReadOnlyList<CustomLoopRunRecord>> ListNonterminalAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CustomLoopRunRecord>>([]);
    public Task<CustomLoopRunStoreResult> UpdateAsync(CustomLoopRunRecord run, int expectedLifecycleVersion, CancellationToken cancellationToken = default) => Task.FromResult(CustomLoopRunStoreResult.NotFound());
}
