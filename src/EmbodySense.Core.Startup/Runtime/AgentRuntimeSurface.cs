using EmbodySense.Core.Common.Runtime;
using EmbodySense.Core.Startup.Runtime.Models;

namespace EmbodySense.Core.Startup.Runtime;

/// <summary>
/// Carries the validated interface-surface identity used by Startup facades and audit attribution.
/// </summary>
public sealed record AgentRuntimeSurface
{
    private AgentRuntimeSurface(RuntimeSurfaceId surfaceId)
    {
        SurfaceId = surfaceId;
    }

    /// <summary>
    /// Gets the validated shared surface value.
    /// </summary>
    public RuntimeSurfaceId SurfaceId { get; }

    /// <summary>
    /// Gets the normalized lowercase surface identifier.
    /// </summary>
    public string Id => SurfaceId.Id;

    /// <summary>
    /// Gets the canonical Web surface.
    /// </summary>
    public static AgentRuntimeSurface Web { get; } = new(RuntimeSurfaceId.Web);

    /// <summary>
    /// Gets the canonical CLI surface.
    /// </summary>
    public static AgentRuntimeSurface Cli { get; } = new(RuntimeSurfaceId.Cli);

    /// <summary>
    /// Creates a runtime surface from a normalized, validated identifier.
    /// </summary>
    /// <param name="id">The surface identifier to normalize and validate.</param>
    /// <returns>A runtime surface carrying the validated identifier.</returns>
    public static AgentRuntimeSurface Create(string id)
    {
        return new AgentRuntimeSurface(RuntimeSurfaceId.Create(id));
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return Id;
    }
}
