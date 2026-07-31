using EmbodySense.Web;
using EmbodySense.Core.Startup.Loops.Execution.Models;
using EmbodySense.Core.Startup.Loops.Execution;
using EmbodySense.Web.Models;
using EmbodySense.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace EmbodySense.Web.Hubs;

/// <summary>
/// Owns the authenticated SignalR connection contract for browser conversation, approval, and custom-loop operations.
/// </summary>
/// <remarks>
/// Each connection is registered as an independent approval owner. Disconnecting rejects that owner's
/// pending approvals but does not cancel an already admitted durable custom-loop run. Conversation turns
/// use the connection-aborted token; durable invocation and resume operations cross the connection lifetime
/// and instead rely on server-owned runtime and approval state.
/// </remarks>
[Authorize(Policy = WebAuthPolicies.LocalSession)]
public sealed class WebSessionHub : Hub<IWebSessionClient>
{
    private readonly WebAgentRuntimeHost _host;
    private readonly WebApprovalCoordinator _approvals;
    private readonly IWebLoopRuntimeInvoker _loopRuntime;

    /// <summary>
    /// Initializes a Web session hub.
    /// </summary>
    /// <param name="host">The shared Web runtime host.</param>
    /// <param name="approvals">The approval coordinator that binds requests to connection ownership.</param>
    /// <param name="loopRuntime">The custom-loop invocation boundary.</param>
    public WebSessionHub(WebAgentRuntimeHost host, WebApprovalCoordinator approvals, IWebLoopRuntimeInvoker loopRuntime)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(approvals);
        ArgumentNullException.ThrowIfNull(loopRuntime);

        _host = host;
        _approvals = approvals;
        _loopRuntime = loopRuntime;
    }

    /// <summary>
    /// Registers the connection as a live approval owner and sends its initial status and approval projections.
    /// </summary>
    /// <returns>A task that completes after connection initialization and base-hub processing.</returns>
    public override async Task OnConnectedAsync()
    {
        _approvals.RegisterOwnerConnection(Context.ConnectionId);
        await Clients.Caller.StatusChanged(_host.GetStatus());
        await Clients.Caller.ApprovalsChanged(_approvals.GetPending(Context.ConnectionId));
        await base.OnConnectedAsync();
    }

    /// <summary>
    /// Removes approval ownership and rejects every still-pending request owned by the disconnected connection.
    /// </summary>
    /// <param name="exception">The transport exception that ended the connection, when present.</param>
    /// <returns>A task that completes after owner cleanup and base-hub processing.</returns>
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await _approvals.DisconnectOwnerAsync(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Idempotently initializes the workspace and broadcasts the resulting status to all authenticated clients.
    /// </summary>
    /// <returns>The post-initialization status.</returns>
    public async Task<WebStatus> InitializeWorkspace()
    {
        var status = await _host.InitializeWorkspaceAsync(Context.ConnectionAborted);
        await Clients.All.StatusChanged(status);
        return status;
    }

    /// <summary>
    /// Gets pending approvals owned by the calling connection in creation order.
    /// </summary>
    /// <returns>The caller-visible approval projection.</returns>
    public Task<IReadOnlyList<WebPendingApproval>> GetPendingApprovals()
    {
        return Task.FromResult(_approvals.GetPending(Context.ConnectionId));
    }

    /// <summary>
    /// Gets the complete canonical transcript after any active turn reaches its publication boundary.
    /// </summary>
    /// <returns>
    /// The current transcript, an empty list for an initialized workspace with no messages, or
    /// <see langword="null"/> when the workspace is not initialized or no transcript is available.
    /// </returns>
    public async Task<IReadOnlyList<WebTranscriptMessage>?> GetCurrentTranscript()
    {
        return await _host.GetCurrentTranscriptAsync(Context.ConnectionAborted);
    }

    /// <summary>
    /// Completes one approval only when it is pending and owned by the calling connection.
    /// </summary>
    /// <param name="requestId">The approval request identity.</param>
    /// <param name="decision">The human decision; a missing payload defaults to rejection.</param>
    /// <returns>The accepted, absent, completed, or unauthorized disposition.</returns>
    public async Task<WebApprovalDecisionResult> DecideApproval(string requestId, WebApprovalDecision? decision)
    {
        return await _approvals.SubmitDecisionAsync(requestId, decision?.Approved ?? false, decision?.Detail, Context.ConnectionId, Context.ConnectionAborted);
    }

    /// <summary>
    /// Sends one message or supported static runtime command through the serialized default-conversation path.
    /// </summary>
    /// <param name="message">The nonblank user message or supported static command.</param>
    /// <param name="requestId">An optional browser-owned idempotency identity.</param>
    /// <returns>A task that completes after final, cancellation, or bounded failure events are sent to the caller.</returns>
    /// <remarks>
    /// Blank input and expected runtime failures are represented as stream events rather than hub errors.
    /// Disconnect cancellation is likewise projected as a cancellation event when the connection can still receive it.
    /// </remarks>
    public async Task SendMessage(string message, string? requestId = null)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            await Clients.Caller.StreamEvent(WebStreamEvent.Failure("Message is required."));
            return;
        }

        try
        {
            await _host.SendMessageAsync(message, (item, _) => Clients.Caller.StreamEvent(item), Context.ConnectionId, Context.ConnectionAborted, requestId);
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

    /// <summary>
    /// Enables or disables verbose runtime-context projection for subsequent conversation turns.
    /// </summary>
    /// <param name="enabled">Whether verbose context events should be emitted.</param>
    /// <returns>A task that completes after a system status or bounded failure event is sent to the caller.</returns>
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

    /// <summary>
    /// Requests cancellation of the currently active default-conversation turn.
    /// </summary>
    /// <returns><see langword="true"/> when a live turn was signalled; otherwise <see langword="false"/>.</returns>
    public Task<bool> CancelCurrentTurn()
    {
        return Task.FromResult(_host.CancelCurrentTurn());
    }

    /// <summary>
    /// Admits a saved custom-loop definition under the calling connection's approval ownership.
    /// </summary>
    /// <param name="input">The exact invocation identity, definition binding, and context selection.</param>
    /// <returns>The durable admission or rejection response.</returns>
    /// <exception cref="HubException">
    /// Invocation is cancelled, uses unsupported persisted evidence, or fails bounded validation or persistence safety checks.
    /// </exception>
    /// <remarks>
    /// Admission is not tied to the SignalR disconnect token. A disconnect rejects connection-owned
    /// approvals while allowing the durable run to continue through its defined failure behavior.
    /// </remarks>
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
        catch (LoopRunEvidenceUnsupportedSchemaException exception)
        {
            throw new HubException($"unsupported_loop_persistence_schema: {exception.Message}");
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or FormatException or IOException)
        {
            throw new HubException("The custom-loop invocation could not be processed safely. Check durable run evidence and the local audit log.");
        }
    }

    /// <summary>
    /// Explicitly resumes a paused custom-loop run under the calling connection's approval ownership.
    /// </summary>
    /// <param name="input">The optimistic, idempotent resume request.</param>
    /// <returns>The durable lifecycle-control response.</returns>
    /// <exception cref="HubException">
    /// Resume is cancelled, uses unsupported persisted evidence, or fails bounded validation or persistence safety checks.
    /// </exception>
    /// <remarks>
    /// Resume is not tied to the SignalR disconnect token. Any approval left pending by a disconnect
    /// is rejected by the coordinator rather than silently transferring ownership.
    /// </remarks>
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
        catch (LoopRunEvidenceUnsupportedSchemaException exception)
        {
            throw new HubException($"unsupported_loop_persistence_schema: {exception.Message}");
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or FormatException or IOException)
        {
            throw new HubException("The custom-loop Resume operation could not be processed safely. Check durable run evidence and the local audit log.");
        }
    }
}
