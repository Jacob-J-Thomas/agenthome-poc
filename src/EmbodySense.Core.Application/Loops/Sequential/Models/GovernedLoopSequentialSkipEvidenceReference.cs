namespace EmbodySense.Core.Application.Loops.Sequential.Models;

/// <summary>Authenticates one append-once topology-pruning event before an exact Ready activation enters Skipped.</summary>
/// <param name="ActivationOrdinal">The exact zero-based activation-history ordinal being pruned.</param>
/// <param name="GoverningActivationOrdinal">The exact earlier terminal activation whose route pruned the incoming edge.</param>
/// <param name="GoverningControlEdgeId">The exact skipped outgoing edge shared with the pruned activation's incoming edges.</param>
/// <param name="OutcomeEvidenceId">The exact retained skip-event identity.</param>
/// <param name="OutcomeEvidenceHash">The lowercase SHA-256 hash of the retained skip event.</param>
public sealed record GovernedLoopSequentialSkipEvidenceReference(
    int ActivationOrdinal,
    int GoverningActivationOrdinal,
    string GoverningControlEdgeId,
    string OutcomeEvidenceId,
    string OutcomeEvidenceHash);
