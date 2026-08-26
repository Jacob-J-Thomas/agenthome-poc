using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Common.HumanReview.Models;

namespace EmbodySense.Core.Application.Tests.HumanReview;

internal sealed class RecordingEffectCertaintySource(params GovernedLoopEffectCertaintySnapshotResult[] results) : IGovernedLoopEffectCertaintySnapshotSource
{
    private readonly Queue<GovernedLoopEffectCertaintySnapshotResult> _results = new(results);

    public int ReadCount { get; private set; }

    public Action<int>? AfterRead { get; set; }

    public Task<GovernedLoopEffectCertaintySnapshotResult> ReadAsync(GovernedLoopEffectCertaintySnapshotQuery query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReadCount++;
        var result = _results.Count == 0 ? new GovernedLoopEffectCertaintySnapshotResult(GovernedLoopEffectCertaintySnapshotStatus.Unavailable) : _results.Dequeue();
        AfterRead?.Invoke(ReadCount);
        return Task.FromResult(result);
    }
}
