using EmbodySense.Core.Application.Loops.EffectAttempts;
using EmbodySense.Core.Application.Loops.EffectAttempts.Models;

namespace EmbodySense.Core.Application.Tests.HumanReview;

internal sealed class RecordingHumanReviewEffectAttemptReadStore(GovernedLoopEffectAttemptReadResult result) : IGovernedLoopEffectAttemptReadStore
{
    public int ReadCount { get; private set; }

    public GovernedLoopEffectAttemptReadResult Result { get; set; } = result;

    public Task<GovernedLoopEffectAttemptReadResult> ReadAsync(string operationId, long effectGeneration, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReadCount++;
        return Task.FromResult(Result);
    }
}
