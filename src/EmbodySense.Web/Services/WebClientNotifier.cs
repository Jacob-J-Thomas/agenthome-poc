using EmbodySense.Web.Models;

namespace EmbodySense.Web.Services;

public sealed class WebClientNotifier : IWebClientNotifier
{
    public static readonly IWebClientNotifier None = new WebClientNotifier();

    private WebClientNotifier()
    {
    }

    public Task ApprovalsChangedAsync(string? ownerConnectionId, IReadOnlyList<WebPendingApproval> approvals, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(approvals);

        return Task.CompletedTask;
    }
}
