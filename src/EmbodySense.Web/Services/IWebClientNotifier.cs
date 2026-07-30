using EmbodySense.Web;
using EmbodySense.Web.Models;

namespace EmbodySense.Web.Services;

/// <summary>
/// Publishes authenticated workspace-state changes to connected Web clients.
/// </summary>
public interface IWebClientNotifier
{
    /// <summary>
    /// Publishes the current pending approvals to the owning connection, or an empty list when ownership is absent.
    /// </summary>
    /// <param name="ownerConnectionId">The owning SignalR connection, or <see langword="null"/> when no connection may receive approvals.</param>
    /// <param name="approvals">The complete current approval projection.</param>
    /// <param name="cancellationToken">The token used to cancel publication.</param>
    /// <returns>A task that completes when publication finishes.</returns>
    Task ApprovalsChangedAsync(string? ownerConnectionId, IReadOnlyList<WebPendingApproval> approvals, CancellationToken cancellationToken = default);

    /// <summary>
    /// Broadcasts a committed default-conversation change to authenticated clients.
    /// </summary>
    /// <param name="notification">The durable conversation-change identity and message count.</param>
    /// <param name="cancellationToken">The token used to cancel publication.</param>
    /// <returns>A task that completes when publication finishes.</returns>
    Task ConversationChangedAsync(WebConversationChanged notification, CancellationToken cancellationToken = default);
}
