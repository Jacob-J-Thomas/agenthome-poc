using EmbodySense.Web;
using EmbodySense.Web.Models;

namespace EmbodySense.Web.Services;

/// <summary>
/// Publishes authenticated workspace-state changes to connected Web clients.
/// </summary>
public interface IWebClientNotifier
{
    /// <summary>
    /// Publishes the current pending approvals to the owning connection, or broadcasts an empty clear when ownership is absent.
    /// </summary>
    /// <param name="ownerConnectionId">The owning SignalR connection, or <see langword="null"/> or whitespace only when broadcasting an empty clear.</param>
    /// <param name="approvals">The complete current approval projection; a nonempty list requires a live owner connection.</param>
    /// <param name="cancellationToken">Reserved for notifier implementations; the SignalR implementation does not observe this token.</param>
    /// <returns>A task that completes when publication finishes.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="approvals"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="approvals"/> is nonempty but <paramref name="ownerConnectionId"/> is null or whitespace.</exception>
    Task ApprovalsChangedAsync(string? ownerConnectionId, IReadOnlyList<WebPendingApproval> approvals, CancellationToken cancellationToken = default);

    /// <summary>
    /// Broadcasts a committed default-conversation change to authenticated clients.
    /// </summary>
    /// <param name="notification">The durable conversation-change identity and message count.</param>
    /// <param name="cancellationToken">Reserved for notifier implementations; the SignalR implementation does not observe this token.</param>
    /// <returns>A task that completes when publication finishes.</returns>
    Task ConversationChangedAsync(WebConversationChanged notification, CancellationToken cancellationToken = default);
}
