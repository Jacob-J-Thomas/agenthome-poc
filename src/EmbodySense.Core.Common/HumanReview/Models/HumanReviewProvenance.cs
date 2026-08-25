namespace EmbodySense.Core.Common.HumanReview.Models;

/// <summary>Retains immutable trusted source correlation for one Human Review contract artifact without carrying credentials or private payloads.</summary>
/// <param name="Kind">The trusted recorder or observation category.</param>
/// <param name="SourceId">The stable canonical source identity.</param>
/// <param name="CorrelationId">The stable canonical trace or operation correlation identity.</param>
/// <param name="ObservedAtUtc">The trusted UTC time at which the source observed the artifact.</param>
/// <param name="ProvenanceHash">The canonical hash of every prior provenance field.</param>
public sealed partial record HumanReviewProvenance(HumanReviewProvenanceKind Kind, string SourceId, string CorrelationId, DateTimeOffset ObservedAtUtc, string ProvenanceHash);
