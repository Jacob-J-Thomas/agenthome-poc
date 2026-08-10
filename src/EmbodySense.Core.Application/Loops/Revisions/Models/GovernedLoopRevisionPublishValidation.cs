using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Application.Loops.Revisions.Models;

/// <summary>Returns one exact server-side publication validation decision.</summary>
/// <param name="Status">The closed validation posture.</param>
/// <param name="OperationId">The exact evaluated operation identifier.</param>
/// <param name="RequestHash">The exact evaluated request hash.</param>
/// <param name="Revision">The exact evaluated immutable revision.</param>
/// <param name="ValidationEvidenceHash">The lowercase SHA-256 validation evidence digest.</param>
public sealed record GovernedLoopRevisionPublishValidation(
    GovernedLoopRevisionPublishValidationStatus Status,
    string OperationId,
    string RequestHash,
    GovernedLoopRevisionReference Revision,
    string ValidationEvidenceHash);
