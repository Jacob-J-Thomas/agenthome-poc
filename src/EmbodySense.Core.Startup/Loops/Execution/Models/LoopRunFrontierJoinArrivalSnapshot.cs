namespace EmbodySense.Core.Startup.Loops.Execution.Models;

/// <summary>Projects one exact predecessor arrival retained for a durable join activation.</summary>
/// <param name="SchemaVersion">The evidence schema version.</param>
/// <param name="ControlEdgeId">The exact incoming control-edge identity.</param>
/// <param name="SourceActivationOrdinal">The zero-based activation that selected the incoming edge.</param>
public sealed record LoopRunFrontierJoinArrivalSnapshot(
    int SchemaVersion,
    string ControlEdgeId,
    int SourceActivationOrdinal);
