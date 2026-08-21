using EmbodySense.Core.Application.Loops.EffectAttempts;
using EmbodySense.Core.Application.Loops.EffectAttempts.Models;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;

namespace EmbodySense.Core.Application.Tests.Loops.Execution.Effects;

internal sealed class ThrowingEffectAttemptStore(bool throwOnResume, bool throwOnBegin) : IGovernedLoopEffectAttemptStore
{
    public Task<GovernedLoopEffectAttemptStoreResult> ResumeAsync(string operationId, long effectGeneration, CancellationToken cancellationToken = default)
        => throwOnResume ? throw new InvalidOperationException("resume-failure") : Task.FromResult(new GovernedLoopEffectAttemptStoreResult(GovernedLoopEffectAttemptStoreStatus.NotFound));

    public Task<GovernedLoopEffectAttemptStoreResult> BeginAsync(GovernedLoopEffectAttempt prepared, CancellationToken cancellationToken = default)
        => throwOnBegin ? throw new InvalidOperationException("begin-failure") : Task.FromResult(new GovernedLoopEffectAttemptStoreResult(GovernedLoopEffectAttemptStoreStatus.Created, prepared, new TestEffectAttemptLease()));

    public Task<GovernedLoopEffectAttemptStoreResult> CompareExchangeAsync(string expectedContentHash, GovernedLoopEffectAttempt replacement, IGovernedLoopEffectAttemptLease lease, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("exchange-failure");
}
