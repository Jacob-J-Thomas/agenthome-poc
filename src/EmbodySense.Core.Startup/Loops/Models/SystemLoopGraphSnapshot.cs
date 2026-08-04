namespace EmbodySense.Core.Startup.Loops.Models;

/// <summary>
/// Projects the canonical topology of a read-only system loop.
/// </summary>
/// <param name="EntryNodeId">The stable entry-node identifier.</param>
/// <param name="TerminalNodeIds">The stable terminal-node identifiers.</param>
/// <param name="Nodes">The canonical graph nodes.</param>
/// <param name="Edges">The canonical graph edges.</param>
public sealed record SystemLoopGraphSnapshot(
    string EntryNodeId,
    IReadOnlyList<string> TerminalNodeIds,
    IReadOnlyList<SystemLoopGraphNodeSnapshot> Nodes,
    IReadOnlyList<SystemLoopGraphEdgeSnapshot> Edges);
