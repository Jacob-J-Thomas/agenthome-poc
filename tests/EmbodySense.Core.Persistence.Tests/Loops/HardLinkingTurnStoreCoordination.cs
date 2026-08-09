using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Persistence.Loops.Models;

namespace EmbodySense.Core.Persistence.Tests.Loops;

internal sealed class HardLinkingTurnStoreCoordination(
    string leasePath,
    string aliasPath,
    DefaultConversationTurnLeasePhase targetPhase = DefaultConversationTurnLeasePhase.AfterValidatedOpenBeforeExclusiveLock) : IDefaultConversationTurnStoreCoordination
{
    private int _linked;

    internal int LinkCount => Volatile.Read(ref _linked);

    public Task BeforeActiveSetOperationAsync(DefaultConversationTurnStoreOperation operation, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task ObserveActiveSetLeasePhaseAsync(
        DefaultConversationTurnStoreOperation operation,
        DefaultConversationTurnLeasePhase phase,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (phase == targetPhase
            && Interlocked.Exchange(ref _linked, 1) == 0)
        {
            UnixHardLink.Create(aliasPath, leasePath);
        }

        return Task.CompletedTask;
    }
}
