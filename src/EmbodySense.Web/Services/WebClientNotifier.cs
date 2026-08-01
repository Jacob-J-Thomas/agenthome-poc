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
    /// Validates the workspace status and completes without publishing it.
    /// </summary>
    /// <param name="status">The required current Web workspace status.</param>
    /// <param name="cancellationToken">Ignored because no asynchronous operation is started.</param>
    /// <returns>An already completed task.</returns>
    public Task StatusChangedAsync(WebStatus status, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(status);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Validates the approval projection and completes without publishing it.
    /// </summary>
    /// <param name="ownerConnectionId">The owning connection, or <see langword="null"/> or whitespace only for an empty clear.</param>
    /// <param name="approvals">The required approval projection; a nonempty list requires an owner connection.</param>
    /// <param name="cancellationToken">Ignored because no asynchronous operation is started.</param>
    /// <returns>An already completed task.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="approvals"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="approvals"/> is nonempty but <paramref name="ownerConnectionId"/> is null or whitespace.</exception>
    public Task ApprovalsChangedAsync(string? ownerConnectionId, IReadOnlyList<WebPendingApproval> approvals, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(approvals);
        if (string.IsNullOrWhiteSpace(ownerConnectionId) && approvals.Count > 0)
        {
            throw new ArgumentException("A nonempty approval projection requires a live owner connection.", nameof(ownerConnectionId));
        }

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
