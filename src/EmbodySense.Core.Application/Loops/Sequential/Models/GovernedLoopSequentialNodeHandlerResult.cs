namespace EmbodySense.Core.Application.Loops.Sequential.Models;

/// <summary>Reports one bounded exact-descriptor handler result.</summary>
/// <param name="Status">The closed handler disposition.</param>
/// <param name="EvidenceHash">The lowercase SHA-256 identity of already-retained outcome or ambiguity evidence.</param>
public sealed record GovernedLoopSequentialNodeHandlerResult(
    GovernedLoopSequentialNodeHandlerResultStatus Status,
    string EvidenceHash);
