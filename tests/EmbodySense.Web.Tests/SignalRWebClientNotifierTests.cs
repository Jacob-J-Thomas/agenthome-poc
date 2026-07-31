using EmbodySense.Web.Models;
using EmbodySense.Web.Services;

namespace EmbodySense.Web.Tests;

public sealed class SignalRWebClientNotifierTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ApprovalsChangedAsync_broadcasts_only_an_ownerless_empty_clear(string? ownerConnectionId)
    {
        var context = new RecordingHubContext();
        var notifier = new SignalRWebClientNotifier(context);

        await notifier.ApprovalsChangedAsync(ownerConnectionId, []);

        var snapshot = Assert.Single(context.ClientsRecorder.AllClient.ApprovalSnapshots);
        Assert.Empty(snapshot);
        Assert.Empty(context.ClientsRecorder.TargetedConnectionIds);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ApprovalsChangedAsync_rejects_an_ownerless_nonempty_projection(string? ownerConnectionId)
    {
        var context = new RecordingHubContext();
        var notifier = new SignalRWebClientNotifier(context);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => notifier.ApprovalsChangedAsync(ownerConnectionId, [CreateApproval()]));

        Assert.Equal("ownerConnectionId", exception.ParamName);
        Assert.Empty(context.ClientsRecorder.AllClient.ApprovalSnapshots);
        Assert.Empty(context.ClientsRecorder.TargetedClient.ApprovalSnapshots);
        Assert.Empty(context.ClientsRecorder.TargetedConnectionIds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApprovalsChangedAsync_publishes_live_owner_projections_only_to_that_connection(bool includeApproval)
    {
        var context = new RecordingHubContext();
        var notifier = new SignalRWebClientNotifier(context);
        IReadOnlyList<WebPendingApproval> approvals = includeApproval ? [CreateApproval()] : [];

        await notifier.ApprovalsChangedAsync("owner-1", approvals);

        Assert.Equal(["owner-1"], context.ClientsRecorder.TargetedConnectionIds);
        Assert.Same(approvals, Assert.Single(context.ClientsRecorder.TargetedClient.ApprovalSnapshots));
        Assert.Empty(context.ClientsRecorder.AllClient.ApprovalSnapshots);
    }

    private static WebPendingApproval CreateApproval()
    {
        return new WebPendingApproval("request-1", 1, DateTimeOffset.UnixEpoch, "read", "private/note.txt", "C:\\workspace\\private\\note.txt", "read", "private", "approval required");
    }

}
