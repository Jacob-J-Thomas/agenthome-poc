using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Persistence.Loops.Models;

namespace EmbodySense.Core.Persistence.Tests.Loops;

internal sealed class SubstitutingTurnStoreCoordination(
    DefaultConversationTurnStoreOperation operation,
    DefaultConversationTurnArchivePhase phase,
    Func<string, Task> substitution) : IDefaultConversationTurnStoreCoordination
{
    private int _substituted;

    public Task BeforeActiveSetOperationAsync(DefaultConversationTurnStoreOperation currentOperation, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public async Task ObserveArchivePhaseAsync(
        DefaultConversationTurnStoreOperation currentOperation,
        string turnId,
        DefaultConversationTurnArchivePhase currentPhase,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (currentOperation == operation && currentPhase == phase && Interlocked.Exchange(ref _substituted, 1) == 0)
        {
            await substitution(turnId);
        }
    }
}
