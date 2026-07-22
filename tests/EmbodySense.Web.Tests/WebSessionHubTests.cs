using System.Security.Claims;
using EmbodySense.Core.Startup.Governance;
using EmbodySense.Core.Startup.Loops.Execution;
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
        approvals.RegisterOwnerConnection("connection-1");
        using var scope = approvals.BeginApprovalScope("connection-1");
        var approvalTask = approvals.RequestApprovalAsync(CreateRequest("req-connect"));
        _ = await WaitForPendingAsync(approvals, "connection-1");

        await hub.OnConnectedAsync();
        var decision = await approvals.SubmitDecisionAsync("req-connect", approved: false, detail: null, decisionConnectionId: "connection-1");
        _ = await approvalTask;

        var status = Assert.Single(clients.CallerClient.Statuses);
        var approvalSnapshot = Assert.Single(clients.CallerClient.ApprovalSnapshots);
        var approval = Assert.Single(approvalSnapshot);
        var transcript = await hub.GetCurrentTranscript();
        Assert.False(status.Initialized);
        Assert.True(decision.Accepted);
        Assert.Equal("req-connect", approval.RequestId);
        Assert.Null(transcript);
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
        Assert.Contains("could not process", streamEvent.Error);
    }

    [Fact]
    public async Task SetVerboseMode_streams_system_status_to_caller()
    {
        using var workspace = new TestWorkspace();
        var approvals = new WebApprovalCoordinator();
        await using var host = CreateHost(workspace.RootPath, approvals);
        var clients = new RecordingHubClients();
        var hub = CreateHub(host, approvals, clients);
        _ = await hub.InitializeWorkspace();

        await hub.SetVerboseMode(true);

        var streamEvent = Assert.Single(clients.CallerClient.StreamEvents);
        Assert.Equal("system", streamEvent.Type);
        Assert.Contains("Verbose mode enabled", streamEvent.Text);
    }

    [Fact]
    public async Task DecideApproval_defaults_missing_decision_to_rejection()
    {
        using var workspace = new TestWorkspace();
        var approvals = new WebApprovalCoordinator();
        await using var host = CreateHost(workspace.RootPath, approvals);
        var clients = new RecordingHubClients();
        var hub = CreateHub(host, approvals, clients);
        await hub.OnConnectedAsync();
        using var scope = approvals.BeginApprovalScope("connection-1");
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
    public async Task OnDisconnectedAsync_rejects_only_that_connections_pending_approvals()
    {
        using var workspace = new TestWorkspace();
        var approvals = new WebApprovalCoordinator();
        await using var host = CreateHost(workspace.RootPath, approvals);
        var firstHub = CreateHub(host, approvals, new RecordingHubClients(), context: new TestHubCallerContext("connection-1"));
        var secondHub = CreateHub(host, approvals, new RecordingHubClients(), context: new TestHubCallerContext("connection-2"));
        await firstHub.OnConnectedAsync();
        await secondHub.OnConnectedAsync();
        Task<(bool Approved, string DecisionBy, string Detail)> firstApproval;
        Task<(bool Approved, string DecisionBy, string Detail)> secondApproval;
        using (approvals.BeginApprovalScope("connection-1"))
        {
            firstApproval = approvals.RequestApprovalAsync(CreateRequest("req-first"));
        }

        using (approvals.BeginApprovalScope("connection-2"))
        {
            secondApproval = approvals.RequestApprovalAsync(CreateRequest("req-second"));
        }

        _ = await WaitForPendingAsync(approvals, "connection-1");
        _ = await WaitForPendingAsync(approvals, "connection-2");

        await firstHub.OnDisconnectedAsync(exception: null);
        var firstResponse = await firstApproval;
        var secondDecision = await approvals.SubmitDecisionAsync("req-second", approved: true, detail: null, decisionConnectionId: "connection-2");
        var secondResponse = await secondApproval;

        Assert.False(firstResponse.Approved);
        Assert.Equal("owner_disconnected", firstResponse.Detail);
        Assert.True(secondDecision.Accepted);
        Assert.True(secondResponse.Approved);
    }

    [Fact]
    public async Task Disconnect_during_custom_loop_approval_returns_denial_to_the_run_without_cancelling_it()
    {
        using var workspace = new TestWorkspace();
        var approvals = new WebApprovalCoordinator();
        await using var host = CreateHost(workspace.RootPath, approvals);
        var invoker = new ApprovalContinuingLoopRuntimeInvoker(approvals);
        var hub = CreateHub(host, approvals, new RecordingHubClients(), invoker);
        await hub.OnConnectedAsync();
        var invocation = new LoopRunInvocationInput("loop-one", 1, new string('a', 64), "invoke-disconnect", "prompt");

        var invocationTask = hub.InvokeLoop(invocation);
        _ = await WaitForPendingAsync(approvals, "connection-1");
        await hub.OnDisconnectedAsync(exception: null);
        var response = await invocationTask;

        Assert.Equal("Completed", response.ExecutionStatus);
        Assert.True(invoker.ContinuedAfterDenial);
        Assert.False(invoker.InvocationCancellationToken.IsCancellationRequested);
        Assert.Equal("owner_disconnected", invoker.ApprovalDetail);
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

    [Fact]
    public async Task Invoke_and_resume_bind_approval_ownership_only_to_the_authenticated_hub_connection()
    {
        using var workspace = new TestWorkspace();
        var approvals = new WebApprovalCoordinator();
        await using var host = CreateHost(workspace.RootPath, approvals);
        var invoker = new RecordingLoopRuntimeInvoker();
        var hub = CreateHub(host, approvals, new RecordingHubClients(), invoker);
        var invocation = new LoopRunInvocationInput("loop-one", 1, new string('a', 64), "invoke-one", "prompt");
        var resume = new LoopRunControlInput("run-one", 2, "resume-one");

        _ = await hub.InvokeLoop(invocation);
        _ = await hub.ResumeLoop(resume);

        Assert.Equal("connection-1", invoker.InvocationOwner);
        Assert.Equal("connection-1", invoker.ResumeOwner);
        Assert.Equal(invocation, invoker.InvocationInput);
        Assert.Equal(resume, invoker.ResumeInput);
    }

    [Theory]
    [InlineData(false, false, "custom-loop invocation could not be processed safely")]
    [InlineData(false, true, "custom-loop invocation was cancelled")]
    [InlineData(true, false, "custom-loop Resume operation could not be processed safely")]
    [InlineData(true, true, "custom-loop Resume operation was cancelled")]
    public async Task Invoke_and_resume_translate_runtime_failures_without_leaking_details(bool resume, bool cancelled, string expectedMessage)
    {
        using var workspace = new TestWorkspace();
        var approvals = new WebApprovalCoordinator();
        await using var host = CreateHost(workspace.RootPath, approvals);
        var hub = CreateHub(host, approvals, new RecordingHubClients(), new ThrowingLoopRuntimeInvoker(cancelled));

        var exception = resume
            ? await Assert.ThrowsAsync<HubException>(() => hub.ResumeLoop(new LoopRunControlInput("run-one", 1, "resume-failure")))
            : await Assert.ThrowsAsync<HubException>(() => hub.InvokeLoop(new LoopRunInvocationInput("loop-one", 1, new string('a', 64), "invoke-failure", "prompt")));

        Assert.Contains(expectedMessage, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive runtime detail", exception.Message, StringComparison.Ordinal);
    }

    private static WebAgentRuntimeHost CreateHost(string rootPath, WebApprovalCoordinator approvals)
    {
        var options = WebRunOptions.FromArguments(["--workdir", rootPath]);
        return new WebAgentRuntimeHost(options, approvals);
    }

    private static WebSessionHub CreateHub(
        WebAgentRuntimeHost host,
        WebApprovalCoordinator approvals,
        RecordingHubClients clients,
        IWebLoopRuntimeInvoker? loopRuntime = null,
        HubCallerContext? context = null)
    {
        return new WebSessionHub(host, approvals, loopRuntime ?? host)
        {
            Clients = clients,
            Context = context ?? new TestHubCallerContext("connection-1")
        };
    }

    private sealed class RecordingLoopRuntimeInvoker : IWebLoopRuntimeInvoker
    {
        public LoopRunInvocationInput? InvocationInput { get; private set; }

        public string? InvocationOwner { get; private set; }

        public LoopRunControlInput? ResumeInput { get; private set; }

        public string? ResumeOwner { get; private set; }

        public Task<LoopRunInvocationResponse> InvokeLoopAsync(LoopRunInvocationInput input, string ownerConnectionId, CancellationToken cancellationToken = default)
        {
            InvocationInput = input;
            InvocationOwner = ownerConnectionId;
            return Task.FromResult(new LoopRunInvocationResponse("Admitted", "Completed", false, null, [], "Recorded invocation."));
        }

        public Task<LoopRunControlResponse> ResumeLoopAsync(LoopRunControlInput input, string ownerConnectionId, CancellationToken cancellationToken = default)
        {
            ResumeInput = input;
            ResumeOwner = ownerConnectionId;
            return Task.FromResult(new LoopRunControlResponse("Completed", null, input.OperationId, "Recorded Resume."));
        }
    }

    private sealed class ApprovalContinuingLoopRuntimeInvoker(WebApprovalCoordinator approvals) : IWebLoopRuntimeInvoker
    {
        public string? ApprovalDetail { get; private set; }

        public bool ContinuedAfterDenial { get; private set; }

        public CancellationToken InvocationCancellationToken { get; private set; }

        public async Task<LoopRunInvocationResponse> InvokeLoopAsync(LoopRunInvocationInput input, string ownerConnectionId, CancellationToken cancellationToken = default)
        {
            InvocationCancellationToken = cancellationToken;
            using var scope = approvals.BeginApprovalScope(ownerConnectionId);
            var approval = await approvals.RequestApprovalAsync(CreateRequest("req-run-disconnect"), cancellationToken);
            ApprovalDetail = approval.Detail;
            ContinuedAfterDenial = !approval.Approved && !cancellationToken.IsCancellationRequested;
            return new LoopRunInvocationResponse("Admitted", "Completed", false, null, [], "Run continued after governed denial.");
        }

        public Task<LoopRunControlResponse> ResumeLoopAsync(LoopRunControlInput input, string ownerConnectionId, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class ThrowingLoopRuntimeInvoker(bool cancelled) : IWebLoopRuntimeInvoker
    {
        public Task<LoopRunInvocationResponse> InvokeLoopAsync(LoopRunInvocationInput input, string ownerConnectionId, CancellationToken cancellationToken = default)
        {
            return Task.FromException<LoopRunInvocationResponse>(CreateException());
        }

        public Task<LoopRunControlResponse> ResumeLoopAsync(LoopRunControlInput input, string ownerConnectionId, CancellationToken cancellationToken = default)
        {
            return Task.FromException<LoopRunControlResponse>(CreateException());
        }

        private Exception CreateException()
        {
            return cancelled
                ? new OperationCanceledException("sensitive runtime detail")
                : new InvalidOperationException("sensitive runtime detail");
        }
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

    private static async Task<IReadOnlyList<WebPendingApproval>> WaitForPendingAsync(WebApprovalCoordinator coordinator, string ownerConnectionId)
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

    private sealed class TestHubCallerContext(string connectionId, CancellationToken connectionAborted = default) : HubCallerContext
    {
        private readonly IFeatureCollection _features = new FeatureCollection();
        private readonly IDictionary<object, object?> _items = new Dictionary<object, object?>();
        private readonly ClaimsPrincipal _user = new(new ClaimsIdentity("test"));

        public override string ConnectionId => connectionId;

        public override string? UserIdentifier => "user-1";

        public override ClaimsPrincipal? User => _user;

        public override IDictionary<object, object?> Items => _items;

        public override IFeatureCollection Features => _features;

        public override CancellationToken ConnectionAborted => connectionAborted;

        public override void Abort()
        {
        }
    }
}
