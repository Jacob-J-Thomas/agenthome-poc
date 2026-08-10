using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Common.Loops.Revisions.Models;

/// <summary>Identifies one exact validated publication without following later lifecycle heads.</summary>
/// <param name="SchemaVersion">The publication-pin schema version.</param>
/// <param name="Revision">The exact immutable published revision.</param>
/// <param name="PublicationOperationId">The idempotent operation that published the revision.</param>
/// <param name="ValidationEvidenceHash">The lowercase SHA-256 digest of the validation evidence used for publication.</param>
public sealed record GovernedLoopRevisionPublicationPin(
    int SchemaVersion,
    GovernedLoopRevisionReference Revision,
    string PublicationOperationId,
    string ValidationEvidenceHash);
