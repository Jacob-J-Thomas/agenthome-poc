using System.Security.Claims;
using EmbodySense.Core.Application.Governance.Permissions.Models;
using EmbodySense.Core.Application.Governance.Tools.Models;
using EmbodySense.Tests.Support;
using EmbodySense.Web.Hubs;
using EmbodySense.Web.Models;
using EmbodySense.Web.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;

namespace EmbodySense.Web.Tests;

public sealed class WebSessionHubTests
{
    [Fact]
    public async Task OnConnectedAsync_pushes_status_and_pending_approvals_to_caller()
    {
        using var workspace = new TestWorkspace();
        var approvals = new WebApprovalCoordinator();
        await using var host = CreateHost(workspace.RootPath, approvals);
        var clients = new RecordingHubClients();
        var hub = CreateHub(host, approvals, clients);
        var approvalTask = approvals.RequestApprovalAsync(CreateRequest("req-connect"));
        _ = await WaitForPendingAsync(approvals);

        await hub.OnConnectedAsync();
        var decision = await approvals.SubmitDecisionAsync("req-connect", approved: false, detail: null);
        _ = await approvalTask;

        var status = Assert.Single(clients.CallerClient.Statuses);
        var approvalSnapshot = Assert.Single(clients.CallerClient.ApprovalSnapshots);
        var approval = Assert.Single(approvalSnapshot);
        Assert.False(status.Initialized);
        Assert.True(decision.Accepted);
        Assert.Equal("req-connect", approval.RequestId);
    }

    [Fact]
    public async Task InitializeWorkspace_initializes_workspace_and_pushes_status_to_all_clients()
    {
        using var workspace = new TestWorkspace();
        var approvals = new WebApprovalCoordinator();
        await using var host = CreateHost(workspace.RootPath, approvals);
        var clients = new RecordingHubClients();
        var hub = CreateHub(host, approvals, clients);

        var status = await hub.InitializeWorkspace();
        var pushedStatus = Assert.Single(clients.AllClient.Statuses);

        Assert.True(status.Initialized);
        Assert.True(pushedStatus.Initialized);
        Assert.True(File.Exists(workspace.File(".agent", "permissions.json")));
    }

    [Fact]
    public async Task SendMessage_streams_error_when_message_is_blank()
    {
        using var workspace = new TestWorkspace();
        var approvals = new WebApprovalCoordinator();
        await using var host = CreateHost(workspace.RootPath, approvals);
        var clients = new RecordingHubClients();
        var hub = CreateHub(host, approvals, clients);

        await hub.SendMessage(" ");

        var streamEvent = Assert.Single(clients.CallerClient.StreamEvents);
        Assert.Equal("error", streamEvent.Type);
        Assert.Equal("Message is required.", streamEvent.Error);
    }

    [Fact]
    public async Task SendMessage_streams_error_when_workspace_is_not_initialized()
    {
        using var workspace = new TestWorkspace();
        var approvals = new WebApprovalCoordinator();
        await using var host = CreateHost(workspace.RootPath, approvals);
        var clients = new RecordingHubClients();
        var hub = CreateHub(host, approvals, clients);

        await hub.SendMessage("hello");

        var streamEvent = Assert.Single(clients.CallerClient.StreamEvents);
        Assert.Equal("error", streamEvent.Type);
        Assert.Contains("Workspace is not initialized", streamEvent.Error);
    }

    [Fact]
    public async Task DecideApproval_defaults_missing_decision_to_rejection()
    {
        using var workspace = new TestWorkspace();
        var approvals = new WebApprovalCoordinator();
        await using var host = CreateHost(workspace.RootPath, approvals);
        var clients = new RecordingHubClients();
        var hub = CreateHub(host, approvals, clients);
        var approvalTask = approvals.RequestApprovalAsync(CreateRequest("req-decision"));
        var pending = await hub.GetPendingApprovals();

        var result = await hub.DecideApproval("req-decision", decision: null);
        var response = await approvalTask;

        Assert.Single(pending);
        Assert.True(result.Accepted);
        Assert.False(response.Approved);
        Assert.Equal("Rejected in the localhost web client.", response.Detail);
    }

    [Fact]
    public async Task CancelCurrentTurn_returns_false_when_no_turn_is_running()
    {
        using var workspace = new TestWorkspace();
        var approvals = new WebApprovalCoordinator();
        await using var host = CreateHost(workspace.RootPath, approvals);
        var clients = new RecordingHubClients();
        var hub = CreateHub(host, approvals, clients);

        Assert.False(await hub.CancelCurrentTurn());
    }

    private static WebAgentRuntimeHost CreateHost(string rootPath, WebApprovalCoordinator approvals)
    {
        var options = WebRunOptions.FromArguments(["--workdir", rootPath]);
        return new WebAgentRuntimeHost(options, approvals);
    }

    private static WebSessionHub CreateHub(WebAgentRuntimeHost host, WebApprovalCoordinator approvals, RecordingHubClients clients)
    {
        return new WebSessionHub(host, approvals)
        {
            Clients = clients,
            Context = new TestHubCallerContext()
        };
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

    private sealed class RecordingWebSessionClient : IWebSessionClient
    {
        public List<WebStatus> Statuses { get; } = [];

        public List<IReadOnlyList<WebPendingApproval>> ApprovalSnapshots { get; } = [];

        public List<WebStreamEvent> StreamEvents { get; } = [];

        public Task StatusChanged(WebStatus status)
        {
            Statuses.Add(status);
            return Task.CompletedTask;
        }

        public Task ApprovalsChanged(IReadOnlyList<WebPendingApproval> approvals)
        {
            ApprovalSnapshots.Add(approvals);
            return Task.CompletedTask;
        }

        public Task StreamEvent(WebStreamEvent streamEvent)
        {
            StreamEvents.Add(streamEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingHubClients : IHubCallerClients<IWebSessionClient>
    {
        private readonly RecordingWebSessionClient _noop = new();

        public RecordingWebSessionClient AllClient { get; } = new();

        public RecordingWebSessionClient CallerClient { get; } = new();

        public IWebSessionClient All => AllClient;

        public IWebSessionClient Caller => CallerClient;

        public IWebSessionClient Others => _noop;

        public IWebSessionClient AllExcept(IReadOnlyList<string> excludedConnectionIds) => _noop;

        public IWebSessionClient Client(string connectionId) => _noop;

        public IWebSessionClient Clients(IReadOnlyList<string> connectionIds) => _noop;

        public IWebSessionClient Group(string groupName) => _noop;

        public IWebSessionClient GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => _noop;

        public IWebSessionClient Groups(IReadOnlyList<string> groupNames) => _noop;

        public IWebSessionClient OthersInGroup(string groupName) => _noop;

        public IWebSessionClient User(string userId) => _noop;

        public IWebSessionClient Users(IReadOnlyList<string> userIds) => _noop;
    }

    private sealed class TestHubCallerContext : HubCallerContext
    {
        private readonly IFeatureCollection _features = new FeatureCollection();
        private readonly IDictionary<object, object?> _items = new Dictionary<object, object?>();
        private readonly ClaimsPrincipal _user = new(new ClaimsIdentity("test"));

        public override string ConnectionId => "connection-1";

        public override string? UserIdentifier => "user-1";

        public override ClaimsPrincipal? User => _user;

        public override IDictionary<object, object?> Items => _items;

        public override IFeatureCollection Features => _features;

        public override CancellationToken ConnectionAborted => CancellationToken.None;

        public override void Abort()
        {
        }
    }
}
