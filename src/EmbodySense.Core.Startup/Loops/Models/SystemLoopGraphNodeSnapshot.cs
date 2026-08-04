using EmbodySense.Core.Common.Loops.Models;

namespace EmbodySense.Core.Startup.Loops.Models;

/// <summary>
/// Projects one canonical node in a read-only system-loop graph.
/// </summary>
/// <param name="Id">The stable graph-node identifier.</param>
/// <param name="DisplayName">The human-readable boundary name.</param>
/// <param name="Description">The implemented boundary semantics.</param>
/// <param name="Kind">The canonical node kind.</param>
/// <param name="EditMode">The node edit mode.</param>
/// <param name="CapabilityIds">The capabilities associated with the boundary.</param>
/// <param name="ExecutionSemantics">How this node relates to the current executor.</param>
public sealed record SystemLoopGraphNodeSnapshot(
    string Id,
    string DisplayName,
    string Description,
    LoopGraphNodeKind Kind,
    LoopGraphNodeEditMode EditMode,
    IReadOnlyList<string> CapabilityIds,
    SystemLoopExecutionSemantics ExecutionSemantics);
