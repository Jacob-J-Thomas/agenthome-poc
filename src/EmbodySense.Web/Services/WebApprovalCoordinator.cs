using System.Collections.Concurrent;
using EmbodySense.Core.Startup.Governance;
using EmbodySense.Web.Models;

namespace EmbodySense.Web.Services;

public sealed class WebApprovalCoordinator : IAgentToolApprovalPrompt
{
    private readonly ConcurrentDictionary<string, PendingApproval> _pending = new(StringComparer.Ordinal);
    private readonly AsyncLocal<string?> _currentOwnerConnectionId = new();
    private readonly IWebClientNotifier _notifier;
    private long _lastSequence;

    public WebApprovalCoordinator(IWebClientNotifier? notifier = null)
    {
        _notifier = notifier ?? WebClientNotifier.None;
    }

    public async Task<(bool Approved, string DecisionBy, string Detail)> RequestApprovalAsync(AgentToolApprovalRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var ownerConnectionId = _currentOwnerConnectionId.Value;
        var pending = new PendingApproval(request, Interlocked.Increment(ref _lastSequence), DateTimeOffset.UtcNow, ownerConnectionId);
        if (!_pending.TryAdd(request.RequestId, pending))
        {
            throw new InvalidOperationException($"Approval request `{request.RequestId}` is already pending.");
        }

        try
        {
            await PublishPendingAsync(ownerConnectionId, CancellationToken.None);
            using var registration = cancellationToken.Register(() => pending.TryCancel(cancellationToken));
            return await pending.WaitAsync(cancellationToken);
        }
        finally
        {
            if (_pending.TryRemove(request.RequestId, out _))
            {
                await PublishPendingAsync(ownerConnectionId, CancellationToken.None);
            }
        }
    }

    public IDisposable BeginApprovalScope(string? ownerConnectionId)
    {
        var previousOwnerConnectionId = _currentOwnerConnectionId.Value;
        _currentOwnerConnectionId.Value = ownerConnectionId;
        return new ApprovalScope(this, previousOwnerConnectionId);
    }

    public IReadOnlyList<WebPendingApproval> GetPending(string? ownerConnectionId = null)
    {
        return _pending.Values
            .Where(item => IsVisibleTo(item, ownerConnectionId))
            .OrderBy(item => item.Sequence)
            .Select(item => WebPendingApproval.FromRequest(item.Request, item.Sequence, item.CreatedAtUtc))
            .ToArray();
    }

    public async Task<WebApprovalDecisionResult> SubmitDecisionAsync(string requestId, bool approved, string? detail, string? decisionConnectionId = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);

        if (!_pending.TryGetValue(requestId, out var pending))
        {
            return WebApprovalDecisionResult.NotFound(requestId);
        }

        if (!IsVisibleTo(pending, decisionConnectionId))
        {
            return WebApprovalDecisionResult.NotAuthorized(requestId);
        }

        if (!_pending.TryRemove(requestId, out pending))
        {
            return WebApprovalDecisionResult.AlreadyCompleted(requestId);
        }

        var responseDetail = string.IsNullOrWhiteSpace(detail)
            ? (approved ? "Approved in the localhost web client." : "Rejected in the localhost web client.")
            : detail.Trim();
        var decisionBy = string.IsNullOrWhiteSpace(decisionConnectionId) ? "human.web" : $"human.web:{decisionConnectionId}";
        var response = (Approved: approved, DecisionBy: decisionBy, Detail: responseDetail);

        var result = pending.TrySetResult(response)
            ? WebApprovalDecisionResult.Completed(requestId)
            : WebApprovalDecisionResult.AlreadyCompleted(requestId);
        await PublishPendingAsync(pending.OwnerConnectionId, CancellationToken.None);
        return result;
    }

    private Task PublishPendingAsync(string? ownerConnectionId, CancellationToken cancellationToken)
    {
        return _notifier.ApprovalsChangedAsync(ownerConnectionId, GetPending(ownerConnectionId), cancellationToken);
    }

    private static bool IsVisibleTo(PendingApproval pending, string? ownerConnectionId)
    {
        return string.IsNullOrWhiteSpace(pending.OwnerConnectionId)
            || string.Equals(pending.OwnerConnectionId, ownerConnectionId, StringComparison.Ordinal);
    }

    private sealed class ApprovalScope : IDisposable
    {
        private readonly WebApprovalCoordinator _coordinator;
        private readonly string? _previousOwnerConnectionId;
        private bool _disposed;

        public ApprovalScope(WebApprovalCoordinator coordinator, string? previousOwnerConnectionId)
        {
            _coordinator = coordinator;
            _previousOwnerConnectionId = previousOwnerConnectionId;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _coordinator._currentOwnerConnectionId.Value = _previousOwnerConnectionId;
            _disposed = true;
        }
    }
}
