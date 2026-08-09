using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Persistence.Loops.Models;

namespace EmbodySense.Core.Persistence.Tests.Loops;

internal sealed class ExitingArchiveTurnStoreCoordination(DefaultConversationTurnArchivePhase exitPhase, int exitCode) : IDefaultConversationTurnStoreCoordination
{
    public Task BeforeActiveSetOperationAsync(DefaultConversationTurnStoreOperation operation, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task ObserveArchivePhaseAsync(DefaultConversationTurnStoreOperation operation, string turnId, DefaultConversationTurnArchivePhase phase, CancellationToken cancellationToken = default)
    {
        if (phase == exitPhase)
        {
            Environment.Exit(exitCode);
        }

        return Task.CompletedTask;
    }
}
