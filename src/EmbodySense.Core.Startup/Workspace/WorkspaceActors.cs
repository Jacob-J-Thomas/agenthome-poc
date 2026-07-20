using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.Runtime.Models;

namespace EmbodySense.Core.Startup.Workspace;

public static class WorkspaceActors
{
    private const string ActorPrefix = "embodysense.";

    public const string Cli = AuditSchema.Actors.Cli;

    public const string Web = AuditSchema.Actors.Web;

    public static string ForSurface(RuntimeSurfaceId surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        return ActorPrefix + surface.Id;
    }
}
