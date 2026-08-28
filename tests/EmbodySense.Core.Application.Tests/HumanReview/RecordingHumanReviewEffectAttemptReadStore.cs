using EmbodySense.Core.Application.Loops.EffectAttempts;
using EmbodySense.Core.Application.Loops.EffectAttempts.Models;

namespace EmbodySense.Core.Application.Tests.HumanReview;

internal sealed class RecordingHumanReviewEffectAttemptReadStore(GovernedLoopEffectAttemptReadResult result) : IGovernedLoopEffectAttemptReadStore
{
    public int ReadCount { get; private set; }

    public string? RequiredWorkspaceId { get; set; }

    public GovernedLoopEffectAttemptReadResult Result { get; set; } = result;

    public string? LastWorkspaceId { get; private set; }

    public Task<GovernedLoopEffectAttemptReadResult> ReadAsync(string workspaceId, string operationId, long effectGeneration, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReadCount++;
        LastWorkspaceId = workspaceId;
        return Task.FromResult(RequiredWorkspaceId is not null && !string.Equals(workspaceId, RequiredWorkspaceId, StringComparison.Ordinal)
            ? new GovernedLoopEffectAttemptReadResult(GovernedLoopEffectAttemptReadStatus.Unavailable)
            : Result);
    }
}
