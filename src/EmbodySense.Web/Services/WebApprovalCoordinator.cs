using EmbodySense.Web;
using EmbodySense.Core.Startup.Governance;
using System.Collections.Concurrent;
using EmbodySense.Web.Models;

namespace EmbodySense.Web.Services;

/// <summary>
/// Coordinates connection-owned, server-timed governed tool approvals for the Web runtime.
/// </summary>
/// <remarks>
/// Approval ownership is captured from an async-local scope established by the hub or host. A request
/// without a live owner is denied before publication. Pending requests cannot transfer between connections:
/// cancellation removes them, owner disconnect rejects them, and a server-owned five-minute deadline
/// rejects them if no decision arrives.
/// </remarks>
public sealed class WebApprovalCoordinator : IAgentToolApprovalPrompt
{
    private static readonly (bool Approved, string DecisionBy, string Detail) _ownerDisconnected = (false, "system.web", "owner_disconnected");
    private static readonly (bool Approved, string DecisionBy, string Detail) _ownerUnavailable = (false, "system.web", "approval_owner_unavailable");
    private static readonly (bool Approved, string DecisionBy, string Detail) _timedOut = (false, "system.web", "approval_timeout");
    private readonly ConcurrentDictionary<string, PendingApproval> _pending = new(StringComparer.Ordinal);
    private readonly HashSet<string> _liveOwnerConnections = new(StringComparer.Ordinal);
    private readonly object _ownerGate = new();
    private readonly AsyncLocal<string?> _currentOwnerConnectionId = new();
    private readonly IWebClientNotifier _notifier;
    private readonly TimeProvider _timeProvider;
    private long _lastSequence;

    /// <summary>
    /// Initializes a coordinator using system time.
    /// </summary>
    /// <param name="notifier">The client notifier, or <see langword="null"/> for a no-op notifier.</param>
    public WebApprovalCoordinator(IWebClientNotifier? notifier = null)
        : this(notifier, TimeProvider.System)
    {
    }

    /// <summary>
    /// Initializes a coordinator with an explicit clock.
    /// </summary>
    /// <param name="notifier">The client notifier, or <see langword="null"/> for a no-op notifier.</param>
    /// <param name="timeProvider">The clock used to enforce the server-owned deadline.</param>
    public WebApprovalCoordinator(IWebClientNotifier? notifier, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        _notifier = notifier ?? WebClientNotifier.None;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Gets the exact server-owned lifetime of a pending approval.
    /// </summary>
    public static TimeSpan ApprovalTimeout => TimeSpan.FromMinutes(5);

    /// <summary>
    /// Publishes one governed tool request to its live scoped owner and waits for a terminal disposition.
    /// </summary>
    /// <param name="request">The unique governed request to authorize.</param>
    /// <param name="cancellationToken">The token that cancels and removes this pending request.</param>
    /// <returns>
    /// The browser decision, an immediate owner-unavailable rejection, an owner-disconnected rejection,
    /// or a server-timeout rejection.
    /// </returns>
    /// <exception cref="InvalidOperationException">The request identity is already pending.</exception>
    /// <exception cref="OperationCanceledException">The supplied token is cancelled while the request is pending.</exception>
    public async Task<(bool Approved, string DecisionBy, string Detail)> RequestApprovalAsync(AgentToolApprovalRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var ownerConnectionId = _currentOwnerConnectionId.Value;
        PendingApproval pending;
        lock (_ownerGate)
        {
            if (string.IsNullOrWhiteSpace(ownerConnectionId) || !_liveOwnerConnections.Contains(ownerConnectionId))
            {
                return _ownerUnavailable;
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

    /// <summary>
    /// Marks a SignalR connection as eligible to own approval requests.
    /// </summary>
    /// <param name="ownerConnectionId">The nonblank server-issued connection identifier.</param>
    public void RegisterOwnerConnection(string ownerConnectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerConnectionId);
        lock (_ownerGate)
        {
            _liveOwnerConnections.Add(ownerConnectionId);
        }
    }

    /// <summary>
    /// Removes a live owner and rejects every pending request still owned by that connection.
    /// </summary>
    /// <param name="ownerConnectionId">The disconnected server-issued connection identifier.</param>
    /// <returns>A task that completes after any changed approval projection is published.</returns>
    /// <remarks>Reconnects receive a new connection identity and cannot decide or revive removed requests.</remarks>
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

                pending.TrySetResult(_ownerDisconnected);
                removedAny = true;
            }
        }

        if (removedAny)
        {
            await PublishPendingAsync(ownerConnectionId, CancellationToken.None);
        }
    }

    /// <summary>
    /// Establishes approval ownership for asynchronous runtime work started inside the returned scope.
    /// </summary>
    /// <param name="ownerConnectionId">The intended owner connection, or <see langword="null"/> to force safe denial.</param>
    /// <returns>A scope that restores the preceding async-local owner when disposed.</returns>
    public IDisposable BeginApprovalScope(string? ownerConnectionId)
    {
        var previousOwnerConnectionId = _currentOwnerConnectionId.Value;
        _currentOwnerConnectionId.Value = ownerConnectionId;
        return new ApprovalScope(_currentOwnerConnectionId, previousOwnerConnectionId);
    }

    /// <summary>
    /// Gets pending requests visible to one owner in stable creation order.
    /// </summary>
    /// <param name="ownerConnectionId">The owner connection identity; null or blank reveals no requests.</param>
    /// <returns>Immutable projections of requests owned by the supplied connection.</returns>
    public IReadOnlyList<WebPendingApproval> GetPending(string? ownerConnectionId = null)
    {
        return _pending.Values
            .Where(item => IsVisibleTo(item, ownerConnectionId))
            .OrderBy(item => item.Sequence)
            .Select(item => WebPendingApproval.FromRequest(item.Request, item.Sequence, item.CreatedAtUtc))
            .ToArray();
    }

    /// <summary>
    /// Attempts to complete one pending request as the deciding live connection.
    /// </summary>
    /// <param name="requestId">The pending request identity.</param>
    /// <param name="approved">Whether the human approved the operation.</param>
    /// <param name="detail">Optional audit detail; a bounded default is generated when blank.</param>
    /// <param name="decisionConnectionId">The live deciding connection identity.</param>
    /// <param name="cancellationToken">
    /// Reserved for notification implementations; cleanup publication is intentionally non-cancellable.
    /// </param>
    /// <returns>
    /// A completed, absent, already-completed, or unauthorized disposition. Null, blank, disconnected,
    /// or non-owning connection identities are unauthorized.
    /// </returns>
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
                pending.TrySetResult(_timedOut);
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
}
