using EmbodySense.Core.Startup.Capabilities;
using EmbodySense.Core.Startup.Capabilities.Models;
using EmbodySense.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EmbodySense.Web.Controllers;

/// <summary>Exposes authenticated no-store capability inspection and exact confirmed lifecycle operations.</summary>
[ApiController]
[Authorize(Policy = WebAuthPolicies.LocalSession)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("api/capabilities")]
public sealed class CapabilitiesController : ControllerBase
{
    private const int MaximumPageSize = 50;
    private readonly ICapabilityCatalogFacade _capabilities;
    private readonly WebAgentRuntimeHost _host;

    /// <summary>Creates the capability controller over the shared Startup facade and Web workspace status.</summary>
    /// <param name="capabilities">The safe surface-neutral capability facade.</param>
    /// <param name="host">The Web runtime host used only for initialized-workspace status.</param>
    public CapabilitiesController(ICapabilityCatalogFacade capabilities, WebAgentRuntimeHost host)
    {
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    /// <summary>Lists one bounded page of safe capability posture.</summary>
    /// <param name="maximumCount">The requested page size from one through fifty.</param>
    /// <param name="cursor">The optional exclusive capability cursor.</param>
    /// <param name="cancellationToken">The token used to cancel the read.</param>
    /// <returns>The catalog page or a bounded error.</returns>
    [HttpGet]
    public async Task<ActionResult<CapabilityPostureCatalogResponse>> List([FromQuery] int maximumCount = MaximumPageSize, [FromQuery] string? cursor = null, CancellationToken cancellationToken = default)
    {
        if (!IsWorkspaceInitialized())
        {
            return WorkspaceNotInitialized();
        }

        return Project(await _capabilities.ReadCatalogAsync(cursor, maximumCount, cancellationToken));
    }

    /// <summary>Reads one exact safe capability posture without accepting the identity in a route path.</summary>
    /// <param name="capabilityId">The canonical capability identity.</param>
    /// <param name="cancellationToken">The token used to cancel the read.</param>
    /// <returns>The exact posture or a bounded error.</returns>
    [HttpGet("detail")]
    public async Task<ActionResult<CapabilityPostureResponse>> Get([FromQuery] string capabilityId, CancellationToken cancellationToken = default)
    {
        if (!IsWorkspaceInitialized())
        {
            return WorkspaceNotInitialized();
        }

        return Project(await _capabilities.ReadAsync(capabilityId, cancellationToken));
    }

    /// <summary>Creates or replays one durable server-owned lifecycle preview.</summary>
    /// <param name="input">The bounded public selection.</param>
    /// <param name="cancellationToken">The token used to cancel preview creation.</param>
    /// <returns>The safe preview or a bounded error.</returns>
    [HttpPost("lifecycle/preview")]
    public async Task<ActionResult<CapabilityLifecyclePreviewResponse>> Preview([FromBody] CapabilityLifecycleSelectionInput input, CancellationToken cancellationToken = default)
    {
        if (!IsWorkspaceInitialized())
        {
            return WorkspaceNotInitialized();
        }

        return Project(await _capabilities.PreviewAsync(input, cancellationToken));
    }

    /// <summary>Explicitly retires one exact durable lifecycle preview without mutation.</summary>
    /// <param name="input">The exact caller-observed preview identities.</param>
    /// <param name="cancellationToken">The token used to cancel before the durable retirement boundary.</param>
    /// <returns>The terminal safe disposition.</returns>
    [HttpPost("lifecycle/discard")]
    public async Task<ActionResult<CapabilityLifecycleMutationResponse>> Discard([FromBody] CapabilityLifecycleDiscardInput input, CancellationToken cancellationToken = default)
    {
        if (!IsWorkspaceInitialized())
        {
            return WorkspaceNotInitialized();
        }

        return Project(await _capabilities.DiscardAsync(input, cancellationToken));
    }

    /// <summary>Explicitly confirms one exact durable lifecycle preview.</summary>
    /// <param name="input">The confirmation and exact caller-observed concurrency identities.</param>
    /// <param name="cancellationToken">The token used to cancel before the durable terminal boundary.</param>
    /// <returns>The terminal safe mutation outcome.</returns>
    [HttpPost("lifecycle/confirm")]
    public async Task<ActionResult<CapabilityLifecycleMutationResponse>> Confirm([FromBody] CapabilityLifecycleConfirmationInput input, CancellationToken cancellationToken = default)
    {
        if (!IsWorkspaceInitialized())
        {
            return WorkspaceNotInitialized();
        }

        var response = await _capabilities.ConfirmAsync(input, cancellationToken);
        if (response.IsCommitted)
        {
            await _host.InvalidateRuntimeAfterCapabilityLifecycleMutationAsync();
        }

        return Project(response);
    }

    private ActionResult<CapabilityPostureCatalogResponse> Project(CapabilityPostureCatalogResponse response)
    {
        return response.Status switch
        {
            "available" or "recovered" => Ok(response),
            "invalid" => BadRequest(response),
            _ => StatusCode(StatusCodes.Status503ServiceUnavailable, response)
        };
    }

    private ActionResult<CapabilityPostureResponse> Project(CapabilityPostureResponse response)
    {
        return response.Status switch
        {
            "available" or "recovered" => Ok(response),
            "invalid" => BadRequest(response),
            "not-found" => NotFound(response),
            _ => StatusCode(StatusCodes.Status503ServiceUnavailable, response)
        };
    }

    private ActionResult<CapabilityLifecyclePreviewResponse> Project(CapabilityLifecyclePreviewResponse response)
    {
        return response.Status switch
        {
            "ready" => Ok(response),
            "invalid" => BadRequest(response),
            "not-found" => NotFound(response),
            "ambiguous" or "conflict" => Conflict(response),
            _ => StatusCode(StatusCodes.Status503ServiceUnavailable, response)
        };
    }

    private ActionResult<CapabilityLifecycleMutationResponse> Project(CapabilityLifecycleMutationResponse response)
    {
        return response.Status switch
        {
            "applied" or "discarded" or "replayed" => Ok(response),
            "invalid" => BadRequest(response),
            "not-found" => NotFound(response),
            "conflict" or "blocked" or "ambiguous" => Conflict(response),
            _ => StatusCode(StatusCodes.Status503ServiceUnavailable, response)
        };
    }

    private ObjectResult WorkspaceNotInitialized() => Conflict(new { error = "workspace_not_initialized", detail = "Initialize the workspace before inspecting or changing capability lifecycle state." });

    private bool IsWorkspaceInitialized() => _host.GetStatus().Initialized;
}
