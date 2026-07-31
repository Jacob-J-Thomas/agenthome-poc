using EmbodySense.Web;
using EmbodySense.Web.Models;

namespace EmbodySense.Web.Hubs;

/// <summary>
/// Defines server-to-client notifications for one authenticated Web SignalR session.
/// </summary>
public interface IWebSessionClient
{
    /// <summary>
    /// Replaces the client's current workspace and host status projection.
    /// </summary>
    /// <param name="status">The complete current status.</param>
    /// <returns>The server-side SignalR dispatch task; client receipt is not acknowledged.</returns>
    Task StatusChanged(WebStatus status);

    /// <summary>
    /// Replaces the client's pending-approval projection.
    /// </summary>
    /// <param name="approvals">The complete approvals visible to this connection.</param>
    /// <returns>The server-side SignalR dispatch task; client receipt is not acknowledged.</returns>
    Task ApprovalsChanged(IReadOnlyList<WebPendingApproval> approvals);

    /// <summary>
    /// Notifies the client that the durable default-conversation transcript changed.
    /// </summary>
    /// <param name="notification">The publication identity and resulting transcript metadata.</param>
    /// <returns>The server-side SignalR dispatch task; client receipt is not acknowledged.</returns>
    Task ConversationChanged(WebConversationChanged notification);

    /// <summary>
    /// Streams one typed default-conversation event to the client.
    /// </summary>
    /// <param name="item">The event payload.</param>
    /// <returns>The server-side SignalR dispatch task; client receipt is not acknowledged.</returns>
    Task StreamEvent(WebStreamEvent item);
}
