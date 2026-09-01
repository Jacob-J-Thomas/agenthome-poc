using EmbodySense.Web;
using EmbodySense.Web.Hubs;
using EmbodySense.Web.Models;
using Microsoft.AspNetCore.SignalR;

namespace EmbodySense.Web.Services;

/// <summary>
/// Broadcasts value-free Human Review refresh hints through the authenticated SignalR session hub.
/// </summary>
public sealed class SignalRWebHumanReviewNotifier : IWebHumanReviewNotifier
{
    private readonly IHubContext<WebSessionHub, IWebSessionClient> _hubContext;

    /// <summary>
    /// Initializes a Human Review notifier.
    /// </summary>
    /// <param name="hubContext">The server-owned typed hub context.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="hubContext"/> is null.</exception>
    public SignalRWebHumanReviewNotifier(IHubContext<WebSessionHub, IWebSessionClient> hubContext)
    {
        ArgumentNullException.ThrowIfNull(hubContext);

        _hubContext = hubContext;
    }

    /// <inheritdoc />
    public Task HumanReviewChangedAsync(WebHumanReviewChanged notification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);

        return _hubContext.Clients.All.HumanReviewChanged(notification);
    }
}
