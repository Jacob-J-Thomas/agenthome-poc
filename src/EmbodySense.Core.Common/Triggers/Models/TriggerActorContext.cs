using EmbodySense.Core.Common.Authority;

namespace EmbodySense.Core.Common.Triggers.Models;

/// <summary>
/// Captures exact actor, surface, workspace, and role evidence without deriving authority from any identifier.
/// </summary>
public sealed record TriggerActorContext
{
    internal TriggerActorContext(AuthorityActorId actorId, string surfaceId, string workspaceId, string roleId)
    {
        ActorId = actorId;
        SurfaceId = surfaceId;
        WorkspaceId = workspaceId;
        RoleId = roleId;
    }

    /// <summary>Gets the exact actor evidence.</summary>
    public AuthorityActorId ActorId { get; }

    /// <summary>Gets the exact surface token.</summary>
    public string SurfaceId { get; }

    /// <summary>Gets the exact workspace token.</summary>
    public string WorkspaceId { get; }

    /// <summary>Gets the exact role token.</summary>
    public string RoleId { get; }
}
