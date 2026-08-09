using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Persistence.Loops.Models;

namespace EmbodySense.Core.Persistence.Tests.Loops;

internal sealed class FailingHistoryStageRetirementCoordination(
    Func<CancellationToken, Task> substitution,
    CancellationTokenSource cancellation,
    bool cancelStaging) : IDefaultConversationTurnStoreCoordination
{
    private int _failed;
    private int _substituted;

    public Task BeforeActiveSetOperationAsync(DefaultConversationTurnStoreOperation operation, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public async Task ObserveArchivePhaseAsync(
        DefaultConversationTurnStoreOperation operation,
        string turnId,
        DefaultConversationTurnArchivePhase phase,
        CancellationToken cancellationToken = default)
    {
        if (operation != DefaultConversationTurnStoreOperation.Update)
        {
            return;
        }

        if (phase == DefaultConversationTurnArchivePhase.AfterPartialHistoryStageWrite
            && Interlocked.Exchange(ref _failed, 1) == 0)
        {
            if (cancelStaging)
            {
                cancellation.Cancel();
                await Task.FromCanceled(cancellation.Token);
            }

            throw new IOException("Injected partial history staging failure.");
        }

        if (phase == DefaultConversationTurnArchivePhase.BeforeIncompleteHistoryStageRetirement
            && Interlocked.Exchange(ref _substituted, 1) == 0)
        {
            await substitution(cancellationToken);
        }
    }
}
