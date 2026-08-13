using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Persistence.Loops.Models;

namespace EmbodySense.CancellationHost.Persistence;

internal sealed class DefaultConversationFileBlockingCoordination(
    string readyPath,
    string releasePath) : IDefaultConversationTurnStoreCoordination
{
    public async Task BeforeActiveSetOperationAsync(DefaultConversationTurnStoreOperation operation, CancellationToken cancellationToken = default)
    {
        await File.WriteAllTextAsync(readyPath, operation.ToString(), cancellationToken);
        while (!File.Exists(releasePath))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(15), cancellationToken);
        }
    }
}
