using EmbodySense.Web;
using EmbodySense.Web.Hubs;
using EmbodySense.Web.Models;

namespace EmbodySense.Web.Tests;

internal sealed class RecordingWebSessionClient : IWebSessionClient
{
    public List<IReadOnlyList<WebPendingApproval>> ApprovalSnapshots { get; } = [];

    public Task StatusChanged(WebStatus status) => Task.CompletedTask;

    public Task ApprovalsChanged(IReadOnlyList<WebPendingApproval> approvals)
    {
        ApprovalSnapshots.Add(approvals);
        return Task.CompletedTask;
    }

    public Task ConversationChanged(WebConversationChanged notification) => Task.CompletedTask;

    public Task StreamEvent(WebStreamEvent item) => Task.CompletedTask;
}
