using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Startup.Runtime.Models;
using EmbodySense.Core.Startup.Workspace;

namespace EmbodySense.Core.Startup.HumanInput;

/// <summary>Creates server-owned Web Human Input authority results without accepting browser identity or claims.</summary>
/// <remarks>The Web host supplies only the result of its authenticated session-scheme check. This helper pins every
/// decision to the canonical Web actor and derives value-free evidence from the exact server-composed operation.</remarks>
public static class HumanInputWebAuthority
{
    /// <summary>Derives the non-secret workspace scope used by the canonical runtime from one configured root.</summary>
    /// <param name="workspaceRoot">The server-owned workspace root.</param>
    /// <returns>The canonical workspace scope identifier.</returns>
    public static string GetWorkspaceId(string workspaceRoot) => CapabilityWorkspaceScopeId.Create(workspaceRoot);

    /// <summary>Returns the canonical Web actor used for authenticated Human Input attribution.</summary>
    /// <returns>The canonical actor, or null only if the fixed contract cannot be parsed.</returns>
    public static AuthorityActorId? GetWebActor()
        => AuthorityActorId.TryParse(WorkspaceActors.Web, out var actor, out _) ? actor : null;

    /// <summary>Creates a lifecycle authorization result for one exact server-composed operation.</summary>
    /// <param name="authenticated">Whether the Web host proved the canonical session scheme for this request.</param>
    /// <param name="operationId">The exact operation identity.</param>
    /// <param name="requestHash">The exact canonical command hash.</param>
    /// <param name="workspaceId">The server-owned workspace identity.</param>
    /// <param name="evaluatedAtUtc">The trusted evaluation instant.</param>
    /// <returns>A pinned actor result with deterministic value-free evidence.</returns>
    public static AgentRuntimeHumanInputLifecycleAuthorization AuthorizeLifecycle(bool authenticated, string operationId, string requestHash, string workspaceId, DateTimeOffset evaluatedAtUtc)
    {
        var actor = GetWebActor();
        if (!authenticated || actor is null)
        {
            return new AgentRuntimeHumanInputLifecycleAuthorization(AgentRuntimeHumanInputAuthorityStatus.Denied, null, string.Empty);
        }

        return new AgentRuntimeHumanInputLifecycleAuthorization(AgentRuntimeHumanInputAuthorityStatus.Ready, actor, Evidence("lifecycle", actor.Value, operationId, requestHash, workspaceId, evaluatedAtUtc));
    }

    /// <summary>Creates a response authentication result for one exact server-composed operation.</summary>
    /// <param name="authenticated">Whether the Web host proved the canonical session scheme for this request.</param>
    /// <param name="operationId">The exact operation identity.</param>
    /// <param name="commandHash">The exact canonical response command hash.</param>
    /// <param name="workspaceId">The server-owned workspace identity.</param>
    /// <param name="evaluatedAtUtc">The trusted evaluation instant.</param>
    /// <returns>A pinned actor result with deterministic value-free evidence.</returns>
    public static AgentRuntimeHumanInputResponseAuthentication AuthenticateResponse(bool authenticated, string operationId, string commandHash, string workspaceId, DateTimeOffset evaluatedAtUtc)
    {
        var actor = GetWebActor();
        if (!authenticated || actor is null)
        {
            return new AgentRuntimeHumanInputResponseAuthentication(AgentRuntimeHumanInputAuthorityStatus.Denied, null, string.Empty);
        }

        return new AgentRuntimeHumanInputResponseAuthentication(AgentRuntimeHumanInputAuthorityStatus.Ready, actor, Evidence("response", actor.Value, operationId, commandHash, workspaceId, evaluatedAtUtc));
    }

    private static string Evidence(string family, string actor, string operationId, string commandHash, string workspaceId, DateTimeOffset evaluatedAtUtc)
    {
        var material = string.Join('\n', "embodysense.human-input.web-authority.v1", family, actor, operationId, commandHash, workspaceId, evaluatedAtUtc.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }
}
