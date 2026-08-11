namespace EmbodySense.Core.Application.Loops.Sequential.Models;

/// <summary>Describes one stable strongly connected component in the immutable topology plan.</summary>
public sealed class GovernedLoopTopologyComponent
{
    internal GovernedLoopTopologyComponent(
        int staticOrdinal,
        string componentId,
        string? cycleId,
        bool isCyclic,
        IReadOnlyList<string> nodeIds,
        int? maximumIterations,
        long? maximumDurationMilliseconds)
    {
        StaticOrdinal = staticOrdinal;
        ComponentId = componentId;
        CycleId = cycleId;
        IsCyclic = isCyclic;
        NodeIds = nodeIds;
        MaximumIterations = maximumIterations;
        MaximumDurationMilliseconds = maximumDurationMilliseconds;
    }

    /// <summary>Gets the zero-based deterministic condensed-graph ordinal.</summary>
    public int StaticOrdinal { get; }
    /// <summary>Gets the stable component identity derived from canonical topology order.</summary>
    public string ComponentId { get; }
    /// <summary>Gets the stable cycle identity, or <see langword="null"/> when the component is acyclic.</summary>
    public string? CycleId { get; }
    /// <summary>Gets whether this component is a bounded cycle.</summary>
    public bool IsCyclic { get; }
    /// <summary>Gets the component node identities in canonical order.</summary>
    public IReadOnlyList<string> NodeIds { get; }
    /// <summary>Gets the most restrictive admitted visit budget for a cycle, or <see langword="null"/> when acyclic.</summary>
    public int? MaximumIterations { get; }
    /// <summary>Gets the most restrictive admitted UTC elapsed-time budget for a cycle, or <see langword="null"/> when acyclic.</summary>
    public long? MaximumDurationMilliseconds { get; }
}
