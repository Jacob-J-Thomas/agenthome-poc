using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.CancellationHost.Persistence;

internal sealed class HumanReviewOrderedReleaseRaceGateStore(ICustomLoopRunStore inner, string readyPath, string releasePath) : ICustomLoopRunStore
{
    private int _barrierEntered;

    public Task<CustomLoopRunStoreResult> CreateAsync(CustomLoopRunRecord run, CancellationToken cancellationToken = default) => inner.CreateAsync(run, cancellationToken);

    public Task<CustomLoopRunRecord?> GetAsync(string runId, CancellationToken cancellationToken = default) => inner.GetAsync(runId, cancellationToken);

    public Task<CustomLoopRunRecord?> GetByAdmissionOperationAsync(string admissionOperationId, CancellationToken cancellationToken = default) => inner.GetByAdmissionOperationAsync(admissionOperationId, cancellationToken);

    public Task<CustomLoopRunRecord?> GetNonterminalByLoopAsync(string loopId, CancellationToken cancellationToken = default) => inner.GetNonterminalByLoopAsync(loopId, cancellationToken);

    public Task<IReadOnlyList<CustomLoopRunSummary>> ListRecentAsync(int maximumCount, CancellationToken cancellationToken = default) => inner.ListRecentAsync(maximumCount, cancellationToken);

    public Task<IReadOnlyList<CustomLoopRunRecord>> ListNonterminalAsync(CancellationToken cancellationToken = default) => inner.ListNonterminalAsync(cancellationToken);

    public async Task<CustomLoopRunStoreResult> UpdateAsync(CustomLoopRunRecord run, int expectedLifecycleVersion, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _barrierEntered, 1) == 0)
        {
            await File.WriteAllTextAsync(readyPath, "ready", cancellationToken);
            await WaitForFileAsync(releasePath, TimeSpan.FromSeconds(30), cancellationToken);
        }

        return await inner.UpdateAsync(run, expectedLifecycleVersion, cancellationToken);
    }

    private static async Task WaitForFileAsync(string path, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCancellation = new CancellationTokenSource(timeout);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCancellation.Token);
        while (!File.Exists(path)) await Task.Delay(TimeSpan.FromMilliseconds(10), linkedCancellation.Token);
    }
}
