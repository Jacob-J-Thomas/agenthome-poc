using EmbodySense.Core.Startup.Governance;
using EmbodySense.Web.Models;
using EmbodySense.Web.Services;

namespace EmbodySense.Web.Tests;

public sealed class WebApprovalCoordinatorTests
{
    [Fact]
    public async Task RequestApprovalAsync_exposes_pending_request_and_returns_browser_decision()
    {
        var notifier = new RecordingNotifier();
        var coordinator = new WebApprovalCoordinator(notifier);
        var request = CreateRequest("req-1");
        coordinator.RegisterOwnerConnection("connection-1");
        using var scope = coordinator.BeginApprovalScope("connection-1");
        var approvalTask = coordinator.RequestApprovalAsync(request);

        var pending = await WaitForPendingAsync(coordinator, "connection-1");
        Assert.Collection(pending, item =>
        {
            Assert.Equal("req-1", item.RequestId);
            Assert.Equal(1, item.Sequence);
            Assert.Equal("read", item.Command);
            Assert.Equal("shared/example.txt", item.TargetPath);
            Assert.Equal(@"C:\workspace\shared\example.txt", item.ResolvedPath);
            Assert.Equal("read", item.Operation);
            Assert.Equal("shared/**", item.MatchedPath);
            Assert.Equal("Needs approval.", item.Reason);
            Assert.True(DateTimeOffset.UtcNow - item.CreatedAtUtc < TimeSpan.FromMinutes(1));
        });

        var result = await coordinator.SubmitDecisionAsync("req-1", approved: true, detail: null, decisionConnectionId: "connection-1");
        var response = await approvalTask;

        Assert.True(result.Accepted);
        Assert.True(response.Approved);
        Assert.Equal("human.web:connection-1", response.DecisionBy);
        Assert.Equal("Approved in the localhost web client.", response.Detail);
        Assert.Empty(coordinator.GetPending("connection-1"));
        Assert.Contains(notifier.Snapshots, snapshot => snapshot.Count == 1 && snapshot[0].RequestId == "req-1");
        Assert.Contains(notifier.Snapshots, snapshot => snapshot.Count == 0);
    }

    [Fact]
    public async Task RequestApprovalAsync_returns_rejection_detail()
    {
        var coordinator = new WebApprovalCoordinator();
        var request = CreateRequest("req-2");
        coordinator.RegisterOwnerConnection("connection-1");
        using var scope = coordinator.BeginApprovalScope("connection-1");
        var approvalTask = coordinator.RequestApprovalAsync(request);
        _ = await WaitForPendingAsync(coordinator, "connection-1");

        var result = await coordinator.SubmitDecisionAsync("req-2", approved: false, detail: "No thanks.", decisionConnectionId: "connection-1");
        var response = await approvalTask;

        Assert.True(result.Accepted);
        Assert.False(response.Approved);
        Assert.Equal("human.web:connection-1", response.DecisionBy);
        Assert.Equal("No thanks.", response.Detail);
    }

    [Fact]
    public async Task RequestApprovalAsync_scopes_pending_request_to_owner_connection()
    {
        var coordinator = new WebApprovalCoordinator();
        coordinator.RegisterOwnerConnection("connection-1");
        coordinator.RegisterOwnerConnection("connection-2");
        using var scope = coordinator.BeginApprovalScope("connection-1");
        var approvalTask = coordinator.RequestApprovalAsync(CreateRequest("req-owned"));
        _ = await WaitForPendingAsync(coordinator, "connection-1");

        var hiddenFromOtherConnection = coordinator.GetPending("connection-2");
        var rejectedDecision = await coordinator.SubmitDecisionAsync("req-owned", approved: true, detail: null, decisionConnectionId: "connection-2");
        var acceptedDecision = await coordinator.SubmitDecisionAsync("req-owned", approved: true, detail: null, decisionConnectionId: "connection-1");
        var response = await approvalTask;

        Assert.Empty(hiddenFromOtherConnection);
        Assert.False(rejectedDecision.Accepted);
        Assert.Contains("another browser connection", rejectedDecision.Message);
        Assert.True(acceptedDecision.Accepted);
        Assert.True(response.Approved);
        Assert.Equal("human.web:connection-1", response.DecisionBy);
    }

    [Fact]
    public async Task RequestApprovalAsync_denies_immediately_when_no_live_owner_exists()
    {
        var coordinator = new WebApprovalCoordinator();
        var approvalTask = coordinator.RequestApprovalAsync(CreateRequest("req-unowned"));
        var response = await approvalTask;

        Assert.False(response.Approved);
        Assert.Equal("system.web", response.DecisionBy);
        Assert.Equal("approval_owner_unavailable", response.Detail);
        Assert.Empty(coordinator.GetPending());
    }

    [Fact]
    public async Task RequestApprovalAsync_removes_cancelled_request()
    {
        var coordinator = new WebApprovalCoordinator();
        coordinator.RegisterOwnerConnection("connection-1");
        using var scope = coordinator.BeginApprovalScope("connection-1");
        using var cancellation = new CancellationTokenSource();
        var approvalTask = coordinator.RequestApprovalAsync(CreateRequest("req-3"), cancellation.Token);
        _ = await WaitForPendingAsync(coordinator, "connection-1");

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => approvalTask);

        Assert.Empty(coordinator.GetPending("connection-1"));
    }

    [Fact]
    public async Task RequestApprovalAsync_rejects_duplicate_pending_request_id()
    {
        var coordinator = new WebApprovalCoordinator();
        coordinator.RegisterOwnerConnection("connection-1");
        using var scope = coordinator.BeginApprovalScope("connection-1");
        var firstTask = coordinator.RequestApprovalAsync(CreateRequest("req-4"));
        _ = await WaitForPendingAsync(coordinator, "connection-1");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
        {
            return coordinator.RequestApprovalAsync(CreateRequest("req-4"));
        });
        var result = await coordinator.SubmitDecisionAsync("req-4", approved: true, detail: null, decisionConnectionId: "connection-1");
        _ = await firstTask;

        Assert.Contains("already pending", exception.Message);
        Assert.True(result.Accepted);
    }

    [Fact]
    public async Task SubmitDecisionAsync_returns_not_found_when_request_is_not_pending()
    {
        var coordinator = new WebApprovalCoordinator();
        coordinator.RegisterOwnerConnection("connection-1");

        var result = await coordinator.SubmitDecisionAsync("missing", approved: true, detail: null, decisionConnectionId: "connection-1");

        Assert.False(result.Accepted);
        Assert.Contains("missing", result.Message);
    }

    [Fact]
    public async Task DisconnectOwnerAsync_rejects_pending_request_and_reconnect_cannot_decide_or_revive_it()
    {
        var notifier = new RecordingNotifier();
        var coordinator = new WebApprovalCoordinator(notifier);
        coordinator.RegisterOwnerConnection("connection-1");
        using var scope = coordinator.BeginApprovalScope("connection-1");
        var approvalTask = coordinator.RequestApprovalAsync(CreateRequest("req-disconnect"));
        _ = await WaitForPendingAsync(coordinator, "connection-1");

        await coordinator.DisconnectOwnerAsync("connection-1");
        coordinator.RegisterOwnerConnection("connection-2");
        var otherDecision = await coordinator.SubmitDecisionAsync("req-disconnect", approved: true, detail: null, decisionConnectionId: "connection-2");
        var response = await approvalTask;
        var laterResponse = await coordinator.RequestApprovalAsync(CreateRequest("req-after-disconnect"));

        Assert.False(response.Approved);
        Assert.Equal("system.web", response.DecisionBy);
        Assert.Equal("owner_disconnected", response.Detail);
        Assert.False(otherDecision.Accepted);
        Assert.Empty(coordinator.GetPending("connection-1"));
        Assert.False(laterResponse.Approved);
        Assert.Equal("approval_owner_unavailable", laterResponse.Detail);
        Assert.Contains(notifier.Notifications, item => item.OwnerConnectionId == "connection-1" && item.Approvals.Count == 0);
    }

    [Fact]
    public async Task RequestApprovalAsync_times_out_after_exact_server_owned_five_minute_deadline()
    {
        var timeProvider = new ImmediateTimerTimeProvider(new DateTimeOffset(2026, 7, 17, 0, 0, 0, TimeSpan.Zero));
        var coordinator = new WebApprovalCoordinator(notifier: null, timeProvider);
        coordinator.RegisterOwnerConnection("connection-1");
        using var scope = coordinator.BeginApprovalScope("connection-1");

        var response = await coordinator.RequestApprovalAsync(CreateRequest("req-timeout"));

        Assert.False(response.Approved);
        Assert.Equal("system.web", response.DecisionBy);
        Assert.Equal("approval_timeout", response.Detail);
        Assert.Equal(TimeSpan.FromMinutes(5), timeProvider.LastDueTime);
        Assert.Equal(TimeSpan.FromMinutes(5), WebApprovalCoordinator.ApprovalTimeout);
        Assert.Empty(coordinator.GetPending("connection-1"));
    }

    [Fact]
    public void Decision_result_factories_describe_result()
    {
        var completed = WebApprovalDecisionResult.Completed("done");
        var alreadyCompleted = WebApprovalDecisionResult.AlreadyCompleted("done");

        Assert.True(completed.Accepted);
        Assert.Contains("completed", completed.Message);
        Assert.False(alreadyCompleted.Accepted);
        Assert.Contains("already completed", alreadyCompleted.Message);
    }

    private static AgentToolApprovalRequest CreateRequest(string id)
    {
        return new AgentToolApprovalRequest(
            id,
            "read",
            "shared/example.txt",
            @"C:\workspace\shared\example.txt",
            "read",
            "shared/**",
            "Needs approval.");
    }

    private static async Task<IReadOnlyList<WebPendingApproval>> WaitForPendingAsync(WebApprovalCoordinator coordinator, string? ownerConnectionId = null)
    {
        for (var i = 0; i < 20; i++)
        {
            var pending = coordinator.GetPending(ownerConnectionId);
            if (pending.Count > 0)
            {
                return pending;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("Approval request was not queued.");
    }

    private sealed class RecordingNotifier : IWebClientNotifier
    {
        public List<(string? OwnerConnectionId, IReadOnlyList<WebPendingApproval> Approvals)> Notifications { get; } = [];

        public IEnumerable<IReadOnlyList<WebPendingApproval>> Snapshots => Notifications.Select(item => item.Approvals);

        public Task ApprovalsChangedAsync(string? ownerConnectionId, IReadOnlyList<WebPendingApproval> approvals, CancellationToken cancellationToken = default)
        {
            Notifications.Add((ownerConnectionId, approvals));
            return Task.CompletedTask;
        }
    }

    private sealed class ImmediateTimerTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public TimeSpan? LastDueTime { get; private set; }

        public override DateTimeOffset GetUtcNow()
        {
            return utcNow;
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            LastDueTime = dueTime;
            ThreadPool.QueueUserWorkItem(_ => callback(state));
            return new NoopTimer();
        }
    }

    private sealed class NoopTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            return true;
        }

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
