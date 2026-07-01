using EmbodySense.Core.Common.Runtime.Models;

namespace EmbodySense.Core.Startup.Runtime.Models;

public sealed record AgentRuntimeSurface
{
    private AgentRuntimeSurface(RuntimeSurfaceId surfaceId)
    {
        SurfaceId = surfaceId;
    }

    public RuntimeSurfaceId SurfaceId { get; }

    public string Id => SurfaceId.Id;

    public static AgentRuntimeSurface Web { get; } = new(RuntimeSurfaceId.Web);

    public static AgentRuntimeSurface Cli { get; } = new(RuntimeSurfaceId.Cli);

    public static AgentRuntimeSurface Create(string id)
    {
        return new AgentRuntimeSurface(RuntimeSurfaceId.Create(id));
    }

    public override string ToString()
    {
        return Id;
    }
}
