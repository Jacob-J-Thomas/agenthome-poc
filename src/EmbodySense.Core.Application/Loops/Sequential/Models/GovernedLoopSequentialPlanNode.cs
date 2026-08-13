using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Application.Loops.Sequential.Models;

/// <summary>Identifies one node in deterministic immutable topology-plan order.</summary>
public sealed class GovernedLoopSequentialPlanNode
{
    internal GovernedLoopSequentialPlanNode(
        int staticOrdinal,
        int ordinal,
        string nodeId,
        GovernedLoopNodeDescriptor descriptor,
        string componentId,
        string? cycleId,
        int componentTraversalOrdinal,
        IReadOnlyList<string> incomingControlEdgeIds,
        IReadOnlyList<string> outgoingControlEdgeIds,
        IReadOnlyDictionary<string, string> parameters,
        string? incomingControlEdgeId,
        string? outgoingControlEdgeId)
    {
        StaticOrdinal = staticOrdinal;
        Ordinal = ordinal;
        NodeId = nodeId;
        Descriptor = descriptor;
        ComponentId = componentId;
        CycleId = cycleId;
        ComponentTraversalOrdinal = componentTraversalOrdinal;
        IncomingControlEdgeIds = incomingControlEdgeIds;
        OutgoingControlEdgeIds = outgoingControlEdgeIds;
        Parameters = parameters;
        IncomingControlEdgeId = incomingControlEdgeId;
        OutgoingControlEdgeId = outgoingControlEdgeId;
    }

    /// <summary>Gets the zero-based stable admission ordinal used to break otherwise-equal scheduling ties.</summary>
    public int StaticOrdinal { get; }

    /// <summary>Gets the legacy linear-plan alias for <see cref="StaticOrdinal"/>.</summary>
    public int Ordinal { get; }

    /// <summary>Gets the exact graph node identity.</summary>
    public string NodeId { get; }

    /// <summary>Gets the exact kind, type identifier, and version.</summary>
    public GovernedLoopNodeDescriptor Descriptor { get; }

    /// <summary>Gets the stable condensed-component identity.</summary>
    public string ComponentId { get; }

    /// <summary>Gets the stable cycle identity, or <see langword="null"/> for an acyclic component.</summary>
    public string? CycleId { get; }

    /// <summary>Gets the zero-based deterministic position within the component's admitted traversal.</summary>
    public int ComponentTraversalOrdinal { get; }

    /// <summary>Gets every exact incoming control-edge identity in canonical order.</summary>
    public IReadOnlyList<string> IncomingControlEdgeIds { get; }

    /// <summary>Gets every exact outgoing control-edge identity in canonical order.</summary>
    public IReadOnlyList<string> OutgoingControlEdgeIds { get; }

    /// <summary>Gets the immutable bounded descriptor parameters admitted with this exact plan node.</summary>
    public IReadOnlyDictionary<string, string> Parameters { get; }

    /// <summary>Gets the sole incoming edge for a linear node, or <see langword="null"/> for an entry or non-linear node.</summary>
    public string? IncomingControlEdgeId { get; }

    /// <summary>Gets the sole outgoing edge for a linear node, or <see langword="null"/> for a terminal or non-linear node.</summary>
    public string? OutgoingControlEdgeId { get; }
}
