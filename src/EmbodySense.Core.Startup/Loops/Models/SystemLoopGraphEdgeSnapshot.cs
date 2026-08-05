using EmbodySense.Core.Common.Loops.Models;

namespace EmbodySense.Core.Startup.Loops.Models;

/// <summary>
/// Projects one canonical edge in a read-only system-loop graph.
/// </summary>
/// <param name="Id">The stable graph-edge identifier.</param>
/// <param name="FromNodeId">The source node identifier.</param>
/// <param name="ToNodeId">The destination node identifier.</param>
/// <param name="Condition">The canonical edge condition.</param>
/// <param name="Description">The implemented transition semantics.</param>
/// <param name="ExecutionSemantics">How this edge relates to the current executor.</param>
public sealed record SystemLoopGraphEdgeSnapshot(
    string Id,
    string FromNodeId,
    string ToNodeId,
    LoopGraphEdgeCondition Condition,
    string Description,
    SystemLoopExecutionSemantics ExecutionSemantics);
