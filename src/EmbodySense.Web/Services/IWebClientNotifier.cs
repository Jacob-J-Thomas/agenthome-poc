using EmbodySense.Web.Models;

namespace EmbodySense.Web.Services;

public interface IWebClientNotifier
{
    Task ApprovalsChangedAsync(IReadOnlyList<WebPendingApproval> approvals, CancellationToken cancellationToken = default);
}
