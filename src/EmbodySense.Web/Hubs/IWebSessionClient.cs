using EmbodySense.Web;
using EmbodySense.Web.Models;

namespace EmbodySense.Web.Hubs;

public interface IWebSessionClient
{
    Task StatusChanged(WebStatus status);

    Task ApprovalsChanged(IReadOnlyList<WebPendingApproval> approvals);

    Task ConversationChanged(WebConversationChanged notification);

    Task StreamEvent(WebStreamEvent item);
}
