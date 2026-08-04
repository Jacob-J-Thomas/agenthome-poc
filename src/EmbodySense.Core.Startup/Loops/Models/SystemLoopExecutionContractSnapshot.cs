namespace EmbodySense.Core.Startup.Loops.Models;

/// <summary>
/// Describes the relationship between a system-loop definition and its current executor.
/// </summary>
/// <param name="Runner">The dedicated runner that implements the current turn transaction.</param>
/// <param name="GraphSemantics">How the projected nodes and edges relate to that runner.</param>
/// <param name="UsesGenericGraphDispatcher">Whether the graph is dispatched node-by-node by a generic graph executor.</param>
/// <param name="Detail">A human-readable explanation of the current execution boundary.</param>
public sealed record SystemLoopExecutionContractSnapshot(
    string Runner,
    SystemLoopExecutionSemantics GraphSemantics,
    bool UsesGenericGraphDispatcher,
    string Detail);
