using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Application.Loops.Sequential.Models;

/// <summary>Identifies one node in deterministic control-edge traversal order.</summary>
public sealed class GovernedLoopSequentialPlanNode
{
    internal GovernedLoopSequentialPlanNode(
        int ordinal,
        string nodeId,
        GovernedLoopNodeDescriptor descriptor,
        string? incomingControlEdgeId,
        string? outgoingControlEdgeId)
    {
        Ordinal = ordinal;
        NodeId = nodeId;
        Descriptor = descriptor;
        IncomingControlEdgeId = incomingControlEdgeId;
        OutgoingControlEdgeId = outgoingControlEdgeId;
    }

    /// <summary>Gets the zero-based execution ordinal.</summary>
    public int Ordinal { get; }

    /// <summary>Gets the exact graph node identity.</summary>
    public string NodeId { get; }

    /// <summary>Gets the exact kind, type identifier, and version.</summary>
    public GovernedLoopNodeDescriptor Descriptor { get; }

    /// <summary>Gets the exact incoming control-edge identity, or <see langword="null"/> for the entry.</summary>
    public string? IncomingControlEdgeId { get; }

    /// <summary>Gets the exact outgoing control-edge identity, or <see langword="null"/> for the terminal.</summary>
    public string? OutgoingControlEdgeId { get; }
}
