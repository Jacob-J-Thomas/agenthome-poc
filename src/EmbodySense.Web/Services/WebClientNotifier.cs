using EmbodySense.Web;
using EmbodySense.Web.Models;

namespace EmbodySense.Web.Services;

/// <summary>
/// Provides the no-op Web notification implementation used outside a connected SignalR host.
/// </summary>
public sealed class WebClientNotifier : IWebClientNotifier
{
    /// <summary>
    /// Gets the shared no-op notifier.
    /// </summary>
    public static readonly IWebClientNotifier None = new WebClientNotifier();

    private WebClientNotifier()
    {
    }

    /// <summary>
    /// Validates the approval projection and completes without publishing it.
    /// </summary>
    /// <param name="ownerConnectionId">Ignored.</param>
    /// <param name="approvals">The required approval projection.</param>
    /// <param name="cancellationToken">Ignored because no asynchronous operation is started.</param>
    /// <returns>An already completed task.</returns>
    public Task ApprovalsChangedAsync(string? ownerConnectionId, IReadOnlyList<WebPendingApproval> approvals, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(approvals);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Validates the conversation notification and completes without publishing it.
    /// </summary>
    /// <param name="notification">The required publication metadata.</param>
    /// <param name="cancellationToken">Ignored because no asynchronous operation is started.</param>
    /// <returns>An already completed task.</returns>
    public Task ConversationChangedAsync(WebConversationChanged notification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);

        return Task.CompletedTask;
    }
}
