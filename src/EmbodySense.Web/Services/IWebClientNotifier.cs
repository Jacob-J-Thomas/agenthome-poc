using EmbodySense.Web.Models;

namespace EmbodySense.Web.Services;

public interface IWebClientNotifier
{
    Task ApprovalsChangedAsync(string? ownerConnectionId, IReadOnlyList<WebPendingApproval> approvals, CancellationToken cancellationToken = default);

    Task ConversationChangedAsync(WebConversationChanged notification, CancellationToken cancellationToken = default);
}
