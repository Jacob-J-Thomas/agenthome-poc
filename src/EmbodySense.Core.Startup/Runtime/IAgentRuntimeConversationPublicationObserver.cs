using EmbodySense.Core.Startup.Runtime.Models;

namespace EmbodySense.Core.Startup.Runtime;

/// <summary>
/// Observes custom-loop output after it has committed to the durable active conversation.
/// </summary>
public interface IAgentRuntimeConversationPublicationObserver
{
    /// <summary>
    /// Handles a committed publication before the invoking surface reports synchronization complete.
    /// </summary>
    /// <param name="publication">The committed conversation and run identity projection.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    Task PublicationCommittedAsync(AgentRuntimeConversationPublication publication, CancellationToken cancellationToken = default);
}
