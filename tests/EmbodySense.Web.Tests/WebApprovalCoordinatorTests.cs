using EmbodySense.Core.Application.Governance.Permissions.Models;
using EmbodySense.Core.Application.Governance.Tools.Models;
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
        var approvalTask = coordinator.RequestApprovalAsync(request);

        var pending = await WaitForPendingAsync(coordinator);
        Assert.Collection(pending, item =>
        {
            Assert.Equal("req-1", item.RequestId);
            Assert.Equal(1, item.Sequence);
            Assert.Equal("read", item.Command);
            Assert.Equal("workspace/shared/example.txt", item.TargetPath);
            Assert.Equal(@"C:\workspace\shared\example.txt", item.ResolvedPath);
            Assert.Equal("read", item.Operation);
            Assert.Equal("workspace/shared/**", item.MatchedPath);
            Assert.Equal("Needs approval.", item.Reason);
            Assert.True(DateTimeOffset.UtcNow - item.CreatedAtUtc < TimeSpan.FromMinutes(1));
        });

        var result = await coordinator.SubmitDecisionAsync("req-1", approved: true, detail: null);
        var response = await approvalTask;

        Assert.True(result.Accepted);
        Assert.True(response.Approved);
        Assert.Equal("human.web", response.DecisionBy);
        Assert.Equal("Approved in the localhost web client.", response.Detail);
        Assert.Empty(coordinator.GetPending());
        Assert.Contains(notifier.Snapshots, snapshot => snapshot.Count == 1 && snapshot[0].RequestId == "req-1");
        Assert.Contains(notifier.Snapshots, snapshot => snapshot.Count == 0);
    }

    [Fact]
    public async Task RequestApprovalAsync_returns_rejection_detail()
    {
        var coordinator = new WebApprovalCoordinator();
        var request = CreateRequest("req-2");
        var approvalTask = coordinator.RequestApprovalAsync(request);
        _ = await WaitForPendingAsync(coordinator);

        var result = await coordinator.SubmitDecisionAsync("req-2", approved: false, detail: "No thanks.");
        var response = await approvalTask;

        Assert.True(result.Accepted);
        Assert.False(response.Approved);
        Assert.Equal("human.web", response.DecisionBy);
        Assert.Equal("No thanks.", response.Detail);
    }

    [Fact]
    public async Task RequestApprovalAsync_removes_cancelled_request()
    {
        var coordinator = new WebApprovalCoordinator();
        using var cancellation = new CancellationTokenSource();
        var approvalTask = coordinator.RequestApprovalAsync(CreateRequest("req-3"), cancellation.Token);
        _ = await WaitForPendingAsync(coordinator);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => approvalTask);

        Assert.Empty(coordinator.GetPending());
    }

    [Fact]
    public async Task RequestApprovalAsync_rejects_duplicate_pending_request_id()
    {
        var coordinator = new WebApprovalCoordinator();
        var firstTask = coordinator.RequestApprovalAsync(CreateRequest("req-4"));
        _ = await WaitForPendingAsync(coordinator);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
        {
            return coordinator.RequestApprovalAsync(CreateRequest("req-4"));
        });
        var result = await coordinator.SubmitDecisionAsync("req-4", approved: true, detail: null);
        _ = await firstTask;

        Assert.Contains("already pending", exception.Message);
        Assert.True(result.Accepted);
    }

    [Fact]
    public async Task SubmitDecisionAsync_returns_not_found_when_request_is_not_pending()
    {
        var coordinator = new WebApprovalCoordinator();

        var result = await coordinator.SubmitDecisionAsync("missing", approved: true, detail: null);

        Assert.False(result.Accepted);
        Assert.Contains("missing", result.Message);
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

    private static ToolApprovalRequest CreateRequest(string id)
    {
        return new ToolApprovalRequest(
            id,
            new ToolRequest(ToolCommand.Read, "workspace/shared/example.txt"),
            @"C:\workspace\shared\example.txt",
            FileSystemOperation.Read,
            PermissionEvaluation.RequiresApproval("workspace/shared/**", "Needs approval."));
    }

    private static async Task<IReadOnlyList<WebPendingApproval>> WaitForPendingAsync(WebApprovalCoordinator coordinator)
    {
        for (var i = 0; i < 20; i++)
        {
            var pending = coordinator.GetPending();
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
        public List<IReadOnlyList<WebPendingApproval>> Snapshots { get; } = [];

        public Task ApprovalsChangedAsync(IReadOnlyList<WebPendingApproval> approvals, CancellationToken cancellationToken = default)
        {
            Snapshots.Add(approvals);
            return Task.CompletedTask;
        }
    }
}
