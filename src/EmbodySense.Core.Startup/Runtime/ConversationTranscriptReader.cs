using EmbodySense.Core.Application.Loops.Execution;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Persistence.Memory;
using EmbodySense.Core.Startup.Runtime.Models;

namespace EmbodySense.Core.Startup.Runtime;

/// <summary>
/// Reads the durable active conversation through the public Startup boundary without constructing an inference runtime.
/// </summary>
public sealed class ConversationTranscriptReader
{
    /// <summary>
    /// Loads the canonical active transcript under the persistence store's coordinated conversation lease.
    /// </summary>
    /// <param name="workingDirectory">The initialized workspace root.</param>
    /// <param name="cancellationToken">Cancels lease acquisition and transcript loading.</param>
    /// <returns>The durable active conversation messages in canonical order.</returns>
    public async Task<IReadOnlyList<AgentRuntimeTranscriptMessage>> ReadCurrentAsync(string workingDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        var paths = new WorkspacePaths(workingDirectory);
        var conversationMemory = new ConversationMemoryStore(paths);
        await new DefaultConversationTurnRecoveryService(
            new DefaultConversationTurnStore(paths),
            conversationMemory,
            new LoopRunStore(paths),
            new FileConversationWorkspaceLease(paths)).RecoverAsync(cancellationToken);
        using (await new FileConversationWorkspaceLease(paths).AcquireAsync(cancellationToken))
        {
            var snapshot = await conversationMemory.LoadCurrentConversationSnapshotAsync(cancellationToken);
            return snapshot.Messages.Select(message => new AgentRuntimeTranscriptMessage(message.Role.ToString(), message.Content)).ToArray();
        }
    }
}
