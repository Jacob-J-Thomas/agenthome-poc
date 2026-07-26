using EmbodySense.Core.Startup.Runtime.Models;

namespace EmbodySense.Core.Startup.Runtime;

public interface IAgentRuntimeConversationPublicationObserver
{
    Task PublicationCommittedAsync(AgentRuntimeConversationPublication publication, CancellationToken cancellationToken = default);
}
