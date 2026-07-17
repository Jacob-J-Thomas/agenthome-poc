using System.Collections.Concurrent;
using EmbodySense.Core.Startup.Governance;
using EmbodySense.Web.Models;

namespace EmbodySense.Web.Services;

public sealed class WebApprovalCoordinator : IAgentToolApprovalPrompt
{
    private static readonly (bool Approved, string DecisionBy, string Detail) OwnerDisconnected = (false, "system.web", "owner_disconnected");
    private static readonly (bool Approved, string DecisionBy, string Detail) OwnerUnavailable = (false, "system.web", "approval_owner_unavailable");
    private static readonly (bool Approved, string DecisionBy, string Detail) TimedOut = (false, "system.web", "approval_timeout");
    private readonly ConcurrentDictionary<string, PendingApproval> _pending = new(StringComparer.Ordinal);
    private readonly HashSet<string> _liveOwnerConnections = new(StringComparer.Ordinal);
    private readonly object _ownerGate = new();
    private readonly AsyncLocal<string?> _currentOwnerConnectionId = new();
    private readonly IWebClientNotifier _notifier;
    private readonly TimeProvider _timeProvider;
    private long _lastSequence;

    public WebApprovalCoordinator(IWebClientNotifier? notifier = null)
        : this(notifier, TimeProvider.System)
    {
    }

    public WebApprovalCoordinator(IWebClientNotifier? notifier, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        _notifier = notifier ?? WebClientNotifier.None;
        _timeProvider = timeProvider;
    }

    public static TimeSpan ApprovalTimeout => TimeSpan.FromMinutes(5);

    public async Task<(bool Approved, string DecisionBy, string Detail)> RequestApprovalAsync(AgentToolApprovalRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var ownerConnectionId = _currentOwnerConnectionId.Value;
        PendingApproval pending;
        lock (_ownerGate)
        {
            if (string.IsNullOrWhiteSpace(ownerConnectionId) || !_liveOwnerConnections.Contains(ownerConnectionId))
            {
                return OwnerUnavailable;
            }

            pending = new PendingApproval(request, Interlocked.Increment(ref _lastSequence), _timeProvider.GetUtcNow(), ownerConnectionId);
            if (!_pending.TryAdd(request.RequestId, pending))
            {
                throw new InvalidOperationException($"Approval request `{request.RequestId}` is already pending.");
            }
        }

        using var timeoutCancellation = new CancellationTokenSource();
        var timeoutTask = EnforceTimeoutAsync(pending, timeoutCancellation.Token);
        try
        {
            await PublishPendingAsync(ownerConnectionId, CancellationToken.None);
            try
            {
                return await pending.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                lock (_ownerGate)
                {
                    if (IsCurrentPending(pending))
                    {
                        pending.TryCancel(cancellationToken);
                    }
                }

                throw;
            }
        }
        finally
        {
            timeoutCancellation.Cancel();
            await timeoutTask;
            if (TryRemoveExact(pending))
            {
                await PublishPendingAsync(ownerConnectionId, CancellationToken.None);
            }
        }
    }

    public void RegisterOwnerConnection(string ownerConnectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerConnectionId);
        lock (_ownerGate)
        {
            _liveOwnerConnections.Add(ownerConnectionId);
        }
    }

    public async Task DisconnectOwnerAsync(string ownerConnectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerConnectionId);

        var removedAny = false;
        lock (_ownerGate)
        {
            _liveOwnerConnections.Remove(ownerConnectionId);
            foreach (var pending in _pending.Values.Where(item => string.Equals(item.OwnerConnectionId, ownerConnectionId, StringComparison.Ordinal)))
            {
                if (!TryRemoveExactCore(pending))
                {
                    continue;
                }

                pending.TrySetResult(OwnerDisconnected);
                removedAny = true;
            }
        }

        if (removedAny)
        {
            await PublishPendingAsync(ownerConnectionId, CancellationToken.None);
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

        WebApprovalDecisionResult result;
        string? notificationOwnerConnectionId = null;
        lock (_ownerGate)
        {
            if (string.IsNullOrWhiteSpace(decisionConnectionId) || !_liveOwnerConnections.Contains(decisionConnectionId))
            {
                return WebApprovalDecisionResult.NotAuthorized(requestId);
            }

            if (!_pending.TryGetValue(requestId, out var pending))
            {
                return WebApprovalDecisionResult.NotFound(requestId);
            }

            if (!string.Equals(pending.OwnerConnectionId, decisionConnectionId, StringComparison.Ordinal))
            {
                return WebApprovalDecisionResult.NotAuthorized(requestId);
            }

            var responseDetail = string.IsNullOrWhiteSpace(detail)
                ? (approved ? "Approved in the localhost web client." : "Rejected in the localhost web client.")
                : detail.Trim();
            var decisionBy = $"human.web:{decisionConnectionId}";
            var response = (Approved: approved, DecisionBy: decisionBy, Detail: responseDetail);
            result = pending.TrySetResult(response)
                ? WebApprovalDecisionResult.Completed(requestId)
                : WebApprovalDecisionResult.AlreadyCompleted(requestId);
            if (result.Accepted && TryRemoveExactCore(pending))
            {
                notificationOwnerConnectionId = pending.OwnerConnectionId;
            }
        }

        if (notificationOwnerConnectionId is not null)
        {
            await PublishPendingAsync(notificationOwnerConnectionId, CancellationToken.None);
        }

        return result;
    }

    private Task PublishPendingAsync(string? ownerConnectionId, CancellationToken cancellationToken)
    {
        return _notifier.ApprovalsChangedAsync(ownerConnectionId, GetPending(ownerConnectionId), cancellationToken);
    }

    private async Task EnforceTimeoutAsync(PendingApproval pending, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(ApprovalTimeout, _timeProvider, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        lock (_ownerGate)
        {
            if (IsCurrentPending(pending))
            {
                pending.TrySetResult(TimedOut);
            }
        }
    }

    private static bool IsVisibleTo(PendingApproval pending, string? ownerConnectionId)
    {
        return !string.IsNullOrWhiteSpace(ownerConnectionId)
            && string.Equals(pending.OwnerConnectionId, ownerConnectionId, StringComparison.Ordinal);
    }

    private bool TryRemoveExact(PendingApproval pending)
    {
        lock (_ownerGate)
        {
            return TryRemoveExactCore(pending);
        }
    }

    private bool TryRemoveExactCore(PendingApproval pending)
    {
        var collection = (ICollection<KeyValuePair<string, PendingApproval>>)_pending;
        return collection.Remove(new KeyValuePair<string, PendingApproval>(pending.Request.RequestId, pending));
    }

    private bool IsCurrentPending(PendingApproval pending)
    {
        return _pending.TryGetValue(pending.Request.RequestId, out var current) && ReferenceEquals(current, pending);
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
