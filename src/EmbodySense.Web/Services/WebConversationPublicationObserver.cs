using EmbodySense.Core.Startup.Runtime;
using EmbodySense.Core.Startup.Runtime.Models;
using EmbodySense.Web.Models;

namespace EmbodySense.Web.Services;

/// <summary>
/// Projects committed default-conversation publication metadata to connected Web clients.
/// </summary>
/// <remarks>
/// Notification occurs only after durable publication commits. Transcript content is not embedded;
/// clients use the operation identity and message count to decide when to refresh canonical history.
/// </remarks>
public sealed class WebConversationPublicationObserver : IAgentRuntimeConversationPublicationObserver
{
    private readonly IWebClientNotifier _notifier;

    /// <summary>
    /// Initializes a conversation publication observer.
    /// </summary>
    /// <param name="notifier">The Web client notification boundary.</param>
    public WebConversationPublicationObserver(IWebClientNotifier notifier)
    {
        _notifier = notifier ?? throw new ArgumentNullException(nameof(notifier));
    }

    /// <summary>
    /// Broadcasts projection metadata for a durably committed conversation publication.
    /// </summary>
    /// <param name="publication">The committed publication identity and transcript metadata.</param>
    /// <param name="cancellationToken">The token used to cancel notification.</param>
    /// <returns>A task that completes when notification finishes.</returns>
    public Task PublicationCommittedAsync(AgentRuntimeConversationPublication publication, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(publication);

        return _notifier.ConversationChangedAsync(
            new WebConversationChanged(publication.OperationId, publication.ConversationId, publication.MessageCount),
            cancellationToken);
    }
}
