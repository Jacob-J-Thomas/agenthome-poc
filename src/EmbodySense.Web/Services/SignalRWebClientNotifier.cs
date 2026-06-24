using EmbodySense.Web.Hubs;
using EmbodySense.Web.Models;
using Microsoft.AspNetCore.SignalR;

namespace EmbodySense.Web.Services;

public sealed class SignalRWebClientNotifier : IWebClientNotifier
{
    private readonly IHubContext<WebSessionHub, IWebSessionClient> _hubContext;

    public SignalRWebClientNotifier(IHubContext<WebSessionHub, IWebSessionClient> hubContext)
    {
        ArgumentNullException.ThrowIfNull(hubContext);

        _hubContext = hubContext;
    }

    public Task ApprovalsChangedAsync(IReadOnlyList<WebPendingApproval> approvals, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(approvals);

        return _hubContext.Clients.All.ApprovalsChanged(approvals);
    }
}
