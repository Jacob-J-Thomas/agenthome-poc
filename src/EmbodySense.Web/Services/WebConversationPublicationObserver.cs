using EmbodySense.Core.Startup.Runtime;
using EmbodySense.Core.Startup.Runtime.Models;
using EmbodySense.Web.Models;

namespace EmbodySense.Web.Services;

public sealed class WebConversationPublicationObserver : IAgentRuntimeConversationPublicationObserver
{
    private readonly IWebClientNotifier _notifier;

    public WebConversationPublicationObserver(IWebClientNotifier notifier)
    {
        _notifier = notifier ?? throw new ArgumentNullException(nameof(notifier));
    }

    public Task PublicationCommittedAsync(AgentRuntimeConversationPublication publication, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(publication);

        return _notifier.ConversationChangedAsync(
            new WebConversationChanged(publication.OperationId, publication.ConversationId, publication.MessageCount),
            cancellationToken);
    }
}
