using EmbodySense.Web;
using EmbodySense.Web.Hubs;
using EmbodySense.Web.Models;
using Microsoft.AspNetCore.SignalR;

namespace EmbodySense.Web.Services;

/// <summary>
/// Publishes Web runtime changes through the typed SignalR session hub.
/// </summary>
public sealed class SignalRWebClientNotifier : IWebClientNotifier
{
    private readonly IHubContext<WebSessionHub, IWebSessionClient> _hubContext;

    /// <summary>
    /// Initializes a SignalR notifier.
    /// </summary>
    /// <param name="hubContext">The server-owned typed hub context.</param>
    public SignalRWebClientNotifier(IHubContext<WebSessionHub, IWebSessionClient> hubContext)
    {
        ArgumentNullException.ThrowIfNull(hubContext);

        _hubContext = hubContext;
    }

    /// <summary>
    /// Replaces the approval projection for one owner, or broadcasts an empty clear when no owner is supplied.
    /// </summary>
    /// <param name="ownerConnectionId">The target connection, or <see langword="null"/> or whitespace only when broadcasting an empty clear.</param>
    /// <param name="approvals">The complete replacement approval list; a nonempty list requires a live owner connection.</param>
    /// <param name="cancellationToken">
    /// Reserved for notifier implementations; the typed SignalR dispatch itself does not observe this token.
    /// </param>
    /// <returns>The SignalR dispatch task.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="approvals"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="approvals"/> is nonempty but <paramref name="ownerConnectionId"/> is null or whitespace.</exception>
    public Task ApprovalsChangedAsync(string? ownerConnectionId, IReadOnlyList<WebPendingApproval> approvals, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(approvals);

        if (string.IsNullOrWhiteSpace(ownerConnectionId))
        {
            if (approvals.Count > 0)
            {
                throw new ArgumentException("A nonempty approval projection requires a live owner connection.", nameof(ownerConnectionId));
            }

            return _hubContext.Clients.All.ApprovalsChanged(approvals);
        }

        return _hubContext.Clients.Client(ownerConnectionId).ApprovalsChanged(approvals);
    }

    /// <summary>
    /// Broadcasts a committed conversation-change notification to all authenticated clients.
    /// </summary>
    /// <param name="notification">The durable publication metadata.</param>
    /// <param name="cancellationToken">
    /// Reserved for notifier implementations; the typed SignalR dispatch itself does not observe this token.
    /// </param>
    /// <returns>The SignalR dispatch task.</returns>
    public Task ConversationChangedAsync(WebConversationChanged notification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);

        return _hubContext.Clients.All.ConversationChanged(notification);
    }
}
