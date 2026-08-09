using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Persistence.Loops.Models;

namespace EmbodySense.Core.Persistence.Tests.Loops;

internal sealed class ReplacingTurnStoreCoordination(string leasePath, string displacedPath, string replacementContent) : IDefaultConversationTurnStoreCoordination
{
    private int _replaced;

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
        if (phase == DefaultConversationTurnLeasePhase.AfterValidatedOpenBeforeExclusiveLock
            && Interlocked.Exchange(ref _replaced, 1) == 0)
        {
            if (OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("Lease pathname replacement is exercised only on Unix hosts.");
            }

            File.Move(leasePath, displacedPath);
            File.WriteAllText(leasePath, replacementContent);
            File.SetUnixFileMode(leasePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        return Task.CompletedTask;
    }
}
