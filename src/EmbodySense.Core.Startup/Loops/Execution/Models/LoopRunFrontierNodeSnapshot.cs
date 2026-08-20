namespace EmbodySense.Core.Startup.Loops.Execution.Models;

/// <summary>Projects one reached node's immutable plan coordinates and durable posture.</summary>
public sealed record LoopRunFrontierNodeSnapshot(
    int SchemaVersion,
    int PlanOrdinal,
    string NodeId,
    string Kind,
    string TypeId,
    int DescriptorVersion,
    IReadOnlyList<string> IncomingControlEdgeIds,
    IReadOnlyList<string> OutgoingControlEdgeIds,
    string Status,
    int? Attempt,
    string? AttemptOperationId,
    string? OutcomeEvidenceId,
    string? OutcomeEvidenceHash)
{
    /// <summary>Gets the zero-based durable activation-history coordinate.</summary>
    public int ActivationOrdinal { get; init; }

    /// <summary>Gets the one-based visit coordinate for this exact graph node.</summary>
    public int VisitOrdinal { get; init; }

    /// <summary>Gets the explicit admitted cycle identity, or <see langword="null"/> for an acyclic activation.</summary>
    public string? CycleId { get; init; }

    /// <summary>Gets the positive cycle iteration paired with <see cref="CycleId"/>, or <see langword="null"/>.</summary>
    public int? CycleIteration { get; init; }

    /// <summary>Gets the exact committed control outcome, or <see langword="null"/> before routing commits.</summary>
    public string? ControlOutcome { get; init; }

    /// <summary>Gets the exact outgoing edges selected by the committed branch decision.</summary>
    public IReadOnlyList<string> SelectedControlEdgeIds { get; init; } = Array.Empty<string>();

    /// <summary>Gets the exact outgoing edges skipped by the committed branch decision.</summary>
    public IReadOnlyList<string> SkippedControlEdgeIds { get; init; } = Array.Empty<string>();

    /// <summary>Gets the exact predecessor arrivals retained for this join activation.</summary>
    public IReadOnlyList<LoopRunFrontierJoinArrivalSnapshot> JoinArrivals { get; init; } = Array.Empty<LoopRunFrontierJoinArrivalSnapshot>();
}
