using EmbodySense.Web.Models;
using EmbodySense.Web.Services;

namespace EmbodySense.Web.Tests;

internal sealed class ApprovalPublicationSignal : IWebClientNotifier
{
    private static readonly TimeSpan _publicationDeadline = TimeSpan.FromSeconds(30);
    private readonly TaskCompletionSource<string> _nonemptyOwnerPublication = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task ApprovalsChangedAsync(string? ownerConnectionId, IReadOnlyList<WebPendingApproval> approvals, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(ownerConnectionId) && approvals.Count > 0)
        {
            _nonemptyOwnerPublication.TrySetResult(ownerConnectionId);
        }

        return Task.CompletedTask;
    }

    public Task ConversationChangedAsync(WebConversationChanged notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<string> WaitForNonemptyApprovalAsync() => _nonemptyOwnerPublication.Task.WaitAsync(_publicationDeadline);
}
