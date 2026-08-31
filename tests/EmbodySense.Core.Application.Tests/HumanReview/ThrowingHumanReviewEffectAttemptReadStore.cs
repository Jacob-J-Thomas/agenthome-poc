using EmbodySense.Core.Application.Loops.EffectAttempts;
using EmbodySense.Core.Application.Loops.EffectAttempts.Models;

namespace EmbodySense.Core.Application.Tests.HumanReview;

internal sealed class ThrowingHumanReviewEffectAttemptReadStore(Exception exception) : IGovernedLoopEffectAttemptReadStore
{
    private readonly Exception _exception = exception;

    public int ReadCount { get; private set; }

    public Task<GovernedLoopEffectAttemptReadResult> ReadAsync(string workspaceId, string operationId, long effectGeneration, CancellationToken cancellationToken = default)
    {
        ReadCount++;
        return Task.FromException<GovernedLoopEffectAttemptReadResult>(_exception);
    }
}
