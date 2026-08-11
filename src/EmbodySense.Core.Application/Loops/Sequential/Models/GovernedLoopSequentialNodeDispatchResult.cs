namespace EmbodySense.Core.Application.Loops.Sequential.Models;

/// <summary>Reports one exact sequential node-dispatch decision.</summary>
/// <param name="Status">The closed dispatch disposition.</param>
/// <param name="EvidenceHash">The handler's exact already-retained evidence identity, or <see langword="null"/> when dispatch was refused.</param>
public sealed record GovernedLoopSequentialNodeDispatchResult(
    GovernedLoopSequentialNodeDispatchStatus Status,
    string? EvidenceHash);
