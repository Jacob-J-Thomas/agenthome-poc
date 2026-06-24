using EmbodySense.Web.Models;

namespace EmbodySense.Web.Hubs;

public interface IWebSessionClient
{
    Task StatusChanged(WebStatus status);

    Task ApprovalsChanged(IReadOnlyList<WebPendingApproval> approvals);

    Task StreamEvent(WebStreamEvent item);
}
