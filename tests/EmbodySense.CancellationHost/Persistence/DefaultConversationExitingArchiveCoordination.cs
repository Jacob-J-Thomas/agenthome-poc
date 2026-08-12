using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Persistence.Loops.Models;

namespace EmbodySense.CancellationHost.Persistence;

internal sealed class DefaultConversationExitingArchiveCoordination(
    DefaultConversationTurnArchivePhase exitPhase,
    int exitCode) : IDefaultConversationTurnStoreCoordination
{
    public Task BeforeActiveSetOperationAsync(DefaultConversationTurnStoreOperation operation, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task ObserveArchivePhaseAsync(
        DefaultConversationTurnStoreOperation operation,
        string turnId,
        DefaultConversationTurnArchivePhase phase,
        CancellationToken cancellationToken = default)
    {
        if (phase == exitPhase)
        {
            Environment.Exit(exitCode);
        }

        return Task.CompletedTask;
    }
}
