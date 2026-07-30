using EmbodySense.Web;
using EmbodySense.Core.Startup.Runtime.Models;
using EmbodySense.Web.Models;
using EmbodySense.Web.Services;

namespace EmbodySense.Web.Tests;

public sealed class WebConversationPublicationObserverTests
{
    [Fact]
    public async Task PublicationCommittedAsync_notifies_clients_with_projection_metadata_only()
    {
        var notifier = new RecordingNotifier();
        var observer = new WebConversationPublicationObserver(notifier);
        var publication = new AgentRuntimeConversationPublication(
            "operation-1",
            "run-1",
            "loop-1",
            "conversation-1",
            3,
            false);

        await observer.PublicationCommittedAsync(publication);

        var notification = Assert.Single(notifier.ConversationChanges);
        Assert.Equal("operation-1", notification.OperationId);
        Assert.Equal("conversation-1", notification.ConversationId);
        Assert.Equal(3, notification.MessageCount);
    }

    [Fact]
    public void Constructor_rejects_missing_notifier()
    {
        Assert.Throws<ArgumentNullException>(() => new WebConversationPublicationObserver(null!));
    }

    [Fact]
    public async Task PublicationCommittedAsync_rejects_missing_publication()
    {
        var observer = new WebConversationPublicationObserver(new RecordingNotifier());

        await Assert.ThrowsAsync<ArgumentNullException>(() => observer.PublicationCommittedAsync(null!));
    }

    private sealed class RecordingNotifier : IWebClientNotifier
    {
        public List<WebConversationChanged> ConversationChanges { get; } = [];

        public Task ApprovalsChangedAsync(string? ownerConnectionId, IReadOnlyList<WebPendingApproval> approvals, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task ConversationChangedAsync(WebConversationChanged notification, CancellationToken cancellationToken = default)
        {
            ConversationChanges.Add(notification);
            return Task.CompletedTask;
        }
    }
}
