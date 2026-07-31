using EmbodySense.Core.Common.Runtime;
using EmbodySense.Core.Common.Governance.Audit;

namespace EmbodySense.Core.Startup.Workspace;

/// <summary>
/// Provides canonical audit actors for startup operations performed by interface surfaces.
/// </summary>
public static class WorkspaceActors
{
    private const string ActorPrefix = "embodysense.";

    /// <summary>
    /// Identifies CLI-owned workspace operations.
    /// </summary>
    public const string Cli = AuditSchema.Actors.Cli;

    /// <summary>
    /// Identifies Web-owned workspace operations.
    /// </summary>
    public const string Web = AuditSchema.Actors.Web;

    /// <summary>
    /// Derives the canonical EmbodySense audit actor for a validated runtime surface.
    /// </summary>
    /// <param name="surface">The normalized runtime surface identity.</param>
    /// <returns>The surface identifier prefixed with <c>embodysense.</c>.</returns>
    public static string ForSurface(RuntimeSurfaceId surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        return ActorPrefix + surface.Id;
    }
}
