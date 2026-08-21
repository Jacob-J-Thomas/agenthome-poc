namespace EmbodySense.Core.Startup.Loops.Execution.Models;

/// <summary>Carries one browser-safe exact governed-loop publication pin using only closed primitive fields.</summary>
/// <param name="SchemaVersion">The publication-pin schema version.</param>
/// <param name="RevisionSchemaVersion">The immutable revision-reference schema version.</param>
/// <param name="GraphId">The exact graph identifier.</param>
/// <param name="RevisionId">The exact immutable revision identifier.</param>
/// <param name="ExecutableHash">The exact executable graph hash.</param>
/// <param name="PublicationOperationId">The operation that published the revision.</param>
/// <param name="ValidationEvidenceHash">The exact validation-evidence hash used for publication.</param>
public sealed record GovernedLoopRevisionPublicationInput(
    int SchemaVersion,
    int RevisionSchemaVersion,
    string GraphId,
    string RevisionId,
    string ExecutableHash,
    string PublicationOperationId,
    string ValidationEvidenceHash);
