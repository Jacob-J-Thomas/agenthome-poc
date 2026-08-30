using EmbodySense.Core.Application.Loops.EffectAuthorityUsage;
using EmbodySense.Core.Application.Loops.EffectAuthorityUsage.Models;

namespace EmbodySense.CancellationHost.Persistence;

internal sealed class HumanReviewOrderedReleaseProcessAuthorityUsageStore : IGovernedLoopEffectAuthorityUsageStore
{
    public Task<GovernedLoopEffectAuthorityUsageStoreResult> ReserveAsync(GovernedLoopEffectAuthorityUsageRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(new GovernedLoopEffectAuthorityUsageStoreResult(GovernedLoopEffectAuthorityUsageStoreStatus.Allowed));

    public Task<GovernedLoopEffectAuthorityUsageStoreResult> BeginCompletionAsync(GovernedLoopEffectAuthorityCompletionUsageRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(new GovernedLoopEffectAuthorityUsageStoreResult(GovernedLoopEffectAuthorityUsageStoreStatus.CompletionPending));

    public Task<GovernedLoopEffectAuthorityUsageStoreResult> CompleteCompletionAsync(GovernedLoopEffectAuthorityCompletionUsageRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(new GovernedLoopEffectAuthorityUsageStoreResult(GovernedLoopEffectAuthorityUsageStoreStatus.CompletionCompleted));
}
