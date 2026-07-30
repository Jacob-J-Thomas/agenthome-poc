using EmbodySense.Web;
using EmbodySense.Web.Hubs;
using EmbodySense.Web.Models;
using EmbodySense.Web.Services;
using Microsoft.AspNetCore.SignalR;

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

    private sealed class RecordingHubContext : IHubContext<WebSessionHub, IWebSessionClient>
    {
        public RecordingHubClients ClientsRecorder { get; } = new();

        public IHubClients<IWebSessionClient> Clients => ClientsRecorder;

        public IGroupManager Groups { get; } = new NoopGroupManager();
    }

    private sealed class RecordingHubClients : IHubClients<IWebSessionClient>
    {
        private readonly RecordingWebSessionClient _noop = new();

        public RecordingWebSessionClient AllClient { get; } = new();

        public RecordingWebSessionClient TargetedClient { get; } = new();

        public List<string> TargetedConnectionIds { get; } = [];

        public IWebSessionClient All => AllClient;

        public IWebSessionClient AllExcept(IReadOnlyList<string> excludedConnectionIds) => _noop;

        public IWebSessionClient Client(string connectionId)
        {
            TargetedConnectionIds.Add(connectionId);
            return TargetedClient;
        }

        public IWebSessionClient Clients(IReadOnlyList<string> connectionIds) => _noop;

        public IWebSessionClient Group(string groupName) => _noop;

        public IWebSessionClient GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => _noop;

        public IWebSessionClient Groups(IReadOnlyList<string> groupNames) => _noop;

        public IWebSessionClient User(string userId) => _noop;

        public IWebSessionClient Users(IReadOnlyList<string> userIds) => _noop;
    }

    private sealed class RecordingWebSessionClient : IWebSessionClient
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

    private sealed class NoopGroupManager : IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
