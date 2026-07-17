using EmbodySense.Core.Startup.Loops.Execution;
using EmbodySense.Web.Models;
using EmbodySense.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace EmbodySense.Web.Hubs;

[Authorize(Policy = WebAuthPolicies.LocalSession)]
public sealed class WebSessionHub : Hub<IWebSessionClient>
{
    private readonly WebAgentRuntimeHost _host;
    private readonly WebApprovalCoordinator _approvals;
    private readonly IWebLoopRuntimeInvoker _loopRuntime;

    public WebSessionHub(WebAgentRuntimeHost host, WebApprovalCoordinator approvals, IWebLoopRuntimeInvoker loopRuntime)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(approvals);
        ArgumentNullException.ThrowIfNull(loopRuntime);

        _host = host;
        _approvals = approvals;
        _loopRuntime = loopRuntime;
    }

    public override async Task OnConnectedAsync()
    {
        _approvals.RegisterOwnerConnection(Context.ConnectionId);
        await Clients.Caller.StatusChanged(_host.GetStatus());
        await Clients.Caller.ApprovalsChanged(_approvals.GetPending(Context.ConnectionId));
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await _approvals.DisconnectOwnerAsync(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    public async Task<WebStatus> InitializeWorkspace()
    {
        var status = await _host.InitializeWorkspaceAsync(Context.ConnectionAborted);
        await Clients.All.StatusChanged(status);
        return status;
    }

    public Task<IReadOnlyList<WebPendingApproval>> GetPendingApprovals()
    {
        return Task.FromResult(_approvals.GetPending(Context.ConnectionId));
    }

    public async Task<WebApprovalDecisionResult> DecideApproval(string requestId, WebApprovalDecision? decision)
    {
        return await _approvals.SubmitDecisionAsync(requestId, decision?.Approved ?? false, decision?.Detail, Context.ConnectionId, Context.ConnectionAborted);
    }

    public async Task SendMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            await Clients.Caller.StreamEvent(WebStreamEvent.Failure("Message is required."));
            return;
        }

        try
        {
            await _host.SendMessageAsync(message, (item, _) => Clients.Caller.StreamEvent(item), Context.ConnectionId, Context.ConnectionAborted);
        }
        catch (OperationCanceledException)
        {
            await Clients.Caller.StreamEvent(WebStreamEvent.Cancelled("Message cancelled."));
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            await Clients.Caller.StreamEvent(WebStreamEvent.Failure("The web runtime could not process that message. Check configuration and audit details for diagnostics."));
        }
    }

    public async Task SetVerboseMode(bool enabled)
    {
        try
        {
            await _host.SetVerboseModeAsync(enabled, (item, _) => Clients.Caller.StreamEvent(item), Context.ConnectionAborted);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            await Clients.Caller.StreamEvent(WebStreamEvent.Failure("Verbose mode requires an initialized workspace."));
        }
    }

    public Task<bool> CancelCurrentTurn()
    {
        return Task.FromResult(_host.CancelCurrentTurn());
    }

    public async Task<LoopRunInvocationResponse> InvokeLoop(LoopRunInvocationInput input)
    {
        try
        {
            return await _loopRuntime.InvokeLoopAsync(input, Context.ConnectionId, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            throw new HubException("The custom-loop invocation was cancelled.");
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or FormatException or IOException)
        {
            throw new HubException("The custom-loop invocation could not be processed safely. Check durable run evidence and the local audit log.");
        }
    }

    public async Task<LoopRunControlResponse> ResumeLoop(LoopRunControlInput input)
    {
        try
        {
            return await _loopRuntime.ResumeLoopAsync(input, Context.ConnectionId, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            throw new HubException("The custom-loop Resume operation was cancelled.");
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or FormatException or IOException)
        {
            throw new HubException("The custom-loop Resume operation could not be processed safely. Check durable run evidence and the local audit log.");
        }
    }
}
