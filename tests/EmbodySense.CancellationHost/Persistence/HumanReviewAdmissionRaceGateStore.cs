using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Persistence.Loops;

namespace EmbodySense.CancellationHost.Persistence;

internal sealed class HumanReviewAdmissionRaceGateStore(CustomLoopRunStore inner, string readyPath, string releasePath) : ICustomLoopRunStore
{
    public Task<CustomLoopRunStoreResult> CreateAsync(CustomLoopRunRecord run, CancellationToken cancellationToken = default) => inner.CreateAsync(run, cancellationToken);

    public async Task<CustomLoopRunRecord?> GetAsync(string runId, CancellationToken cancellationToken = default)
    {
        var loaded = await inner.GetAsync(runId, cancellationToken);
        await File.WriteAllTextAsync(readyPath, "ready", cancellationToken);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!File.Exists(releasePath))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
        }

        return loaded;
    }

    public Task<CustomLoopRunRecord?> GetByAdmissionOperationAsync(string admissionOperationId, CancellationToken cancellationToken = default) => inner.GetByAdmissionOperationAsync(admissionOperationId, cancellationToken);
    public Task<CustomLoopRunRecord?> GetNonterminalByLoopAsync(string loopId, CancellationToken cancellationToken = default) => inner.GetNonterminalByLoopAsync(loopId, cancellationToken);
    public Task<IReadOnlyList<CustomLoopRunSummary>> ListRecentAsync(int maximumCount, CancellationToken cancellationToken = default) => inner.ListRecentAsync(maximumCount, cancellationToken);
    public Task<IReadOnlyList<CustomLoopRunRecord>> ListNonterminalAsync(CancellationToken cancellationToken = default) => inner.ListNonterminalAsync(cancellationToken);
    public Task<CustomLoopRunStoreResult> UpdateAsync(CustomLoopRunRecord run, int expectedLifecycleVersion, CancellationToken cancellationToken = default) => inner.UpdateAsync(run, expectedLifecycleVersion, cancellationToken);
}
