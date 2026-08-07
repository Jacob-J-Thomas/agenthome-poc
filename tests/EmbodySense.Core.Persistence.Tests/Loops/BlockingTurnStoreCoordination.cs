using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Persistence.Loops.Models;

namespace EmbodySense.Core.Persistence.Tests.Loops;

internal sealed class BlockingTurnStoreCoordination(DefaultConversationTurnStoreOperation blockedOperation) : IDefaultConversationTurnStoreCoordination
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task BeforeActiveSetOperationAsync(DefaultConversationTurnStoreOperation operation, CancellationToken cancellationToken = default)
    {
        if (operation != blockedOperation)
        {
            return;
        }

        _entered.TrySetResult();
        await _released.Task.WaitAsync(cancellationToken);
    }

    public Task WaitUntilBlockedAsync() => _entered.Task;

    public void Release() => _released.TrySetResult();
}
