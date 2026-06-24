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

    public WebSessionHub(WebAgentRuntimeHost host, WebApprovalCoordinator approvals)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(approvals);

        _host = host;
        _approvals = approvals;
    }

    public override async Task OnConnectedAsync()
    {
        await Clients.Caller.StatusChanged(_host.GetStatus());
        await Clients.Caller.ApprovalsChanged(_approvals.GetPending());
        await base.OnConnectedAsync();
    }

    public async Task<WebStatus> InitializeWorkspace()
    {
        var status = await _host.InitializeWorkspaceAsync(Context.ConnectionAborted);
        await Clients.All.StatusChanged(status);
        return status;
    }

    public Task<IReadOnlyList<WebPendingApproval>> GetPendingApprovals()
    {
        return Task.FromResult(_approvals.GetPending());
    }

    public async Task<WebApprovalDecisionResult> DecideApproval(string requestId, WebApprovalDecision? decision)
    {
        return await _approvals.SubmitDecisionAsync(requestId, decision?.Approved ?? false, decision?.Detail, Context.ConnectionAborted);
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
            await _host.SendMessageAsync(message, (item, _) => Clients.Caller.StreamEvent(item), Context.ConnectionAborted);
        }
        catch (OperationCanceledException)
        {
            await Clients.Caller.StreamEvent(WebStreamEvent.Cancelled("Message cancelled."));
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            await Clients.Caller.StreamEvent(WebStreamEvent.Failure(exception.Message));
        }
    }

    public Task<bool> CancelCurrentTurn()
    {
        return Task.FromResult(_host.CancelCurrentTurn());
    }
}
