using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Application.Loops.Sequential;

/// <summary>Snapshots bounded handlers under exact case-sensitive kind, type identifier, and version keys.</summary>
public sealed class GovernedLoopSequentialNodeHandlerRegistry
{
    private const int MaximumHandlers = 3;
    private readonly IReadOnlyDictionary<(GovernedLoopNodeKind Kind, string TypeId, int Version), IGovernedLoopSequentialNodeHandler> _handlers;

    /// <summary>Creates a bounded exact registry and rejects null, duplicate, or unsupported registrations.</summary>
    public GovernedLoopSequentialNodeHandlerRegistry(IEnumerable<IGovernedLoopSequentialNodeHandler> handlers)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        var snapshot = handlers.Take(MaximumHandlers + 1).ToArray();
        if (snapshot.Length > MaximumHandlers
            || snapshot.Any(handler => handler is null || !GovernedLoopSequentialNodeDescriptors.IsSupported(handler.Descriptor)))
        {
            throw new ArgumentException("Sequential handlers must be non-null and use one exact supported descriptor.", nameof(handlers));
        }

        var registrations = new Dictionary<(GovernedLoopNodeKind Kind, string TypeId, int Version), IGovernedLoopSequentialNodeHandler>();
        foreach (var handler in snapshot)
        {
            var descriptor = handler.Descriptor;
            if (!registrations.TryAdd((descriptor.Kind, descriptor.TypeId, descriptor.Version), handler))
            {
                throw new ArgumentException("Sequential handler descriptors must be unique.", nameof(handlers));
            }
        }

        _handlers = registrations;
    }

    /// <summary>Resolves only an exact case-sensitive kind, type identifier, and version registration.</summary>
    public bool TryResolve(GovernedLoopNodeDescriptor? descriptor, out IGovernedLoopSequentialNodeHandler? handler)
    {
        if (descriptor is null || !GovernedLoopSequentialNodeDescriptors.IsSupported(descriptor))
        {
            handler = null;
            return false;
        }

        return _handlers.TryGetValue((descriptor.Kind, descriptor.TypeId, descriptor.Version), out handler);
    }
}
