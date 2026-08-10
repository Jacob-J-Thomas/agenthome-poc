using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Application.Loops.Revisions.Models;

/// <summary>Binds server-side publication validation to one exact immutable artifact and lifecycle request.</summary>
/// <param name="OperationId">The exact lifecycle operation identifier.</param>
/// <param name="RequestHash">The server-computed canonical request hash.</param>
/// <param name="Kind">The publish or rollback operation being validated.</param>
/// <param name="Artifact">The exact immutable artifact selected for publication.</param>
/// <param name="EvaluatedAtUtc">The trusted UTC evaluation instant.</param>
public sealed record GovernedLoopRevisionPublishValidationRequest(
    string OperationId,
    string RequestHash,
    GovernedLoopRevisionOperationKind Kind,
    GovernedLoopRevisionArtifact Artifact,
    DateTimeOffset EvaluatedAtUtc);
