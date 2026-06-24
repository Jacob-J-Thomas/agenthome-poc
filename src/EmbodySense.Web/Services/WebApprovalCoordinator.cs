using System.Collections.Concurrent;
using EmbodySense.Core.Startup.Governance;
using EmbodySense.Web.Models;

namespace EmbodySense.Web.Services;

public sealed class WebApprovalCoordinator : IAgentToolApprovalPrompt
{
    private readonly ConcurrentDictionary<string, PendingApproval> _pending = new(StringComparer.Ordinal);
    private readonly IWebClientNotifier _notifier;
    private long _lastSequence;

    public WebApprovalCoordinator(IWebClientNotifier? notifier = null)
    {
        _notifier = notifier ?? WebClientNotifier.None;
    }

    public async Task<(bool Approved, string DecisionBy, string Detail)> RequestApprovalAsync(AgentToolApprovalRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var pending = new PendingApproval(request, Interlocked.Increment(ref _lastSequence), DateTimeOffset.UtcNow);
        if (!_pending.TryAdd(request.RequestId, pending))
        {
            throw new InvalidOperationException($"Approval request `{request.RequestId}` is already pending.");
        }

        try
        {
            await PublishPendingAsync(CancellationToken.None);
            using var registration = cancellationToken.Register(() => pending.TryCancel(cancellationToken));
            return await pending.WaitAsync(cancellationToken);
        }
        finally
        {
            if (_pending.TryRemove(request.RequestId, out _))
            {
                await PublishPendingAsync(CancellationToken.None);
            }
        }
    }

    public IReadOnlyList<WebPendingApproval> GetPending()
    {
        return _pending.Values
            .OrderBy(item => item.Sequence)
            .Select(item => WebPendingApproval.FromRequest(item.Request, item.Sequence, item.CreatedAtUtc))
            .ToArray();
    }

    public async Task<WebApprovalDecisionResult> SubmitDecisionAsync(string requestId, bool approved, string? detail, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);

        if (!_pending.TryRemove(requestId, out var pending))
        {
            return WebApprovalDecisionResult.NotFound(requestId);
        }

        var responseDetail = string.IsNullOrWhiteSpace(detail)
            ? (approved ? "Approved in the localhost web client." : "Rejected in the localhost web client.")
            : detail.Trim();
        var response = (Approved: approved, DecisionBy: "human.web", Detail: responseDetail);

        var result = pending.TrySetResult(response)
            ? WebApprovalDecisionResult.Completed(requestId)
            : WebApprovalDecisionResult.AlreadyCompleted(requestId);
        await PublishPendingAsync(CancellationToken.None);
        return result;
    }

    private Task PublishPendingAsync(CancellationToken cancellationToken)
    {
        return _notifier.ApprovalsChangedAsync(GetPending(), cancellationToken);
    }
}
