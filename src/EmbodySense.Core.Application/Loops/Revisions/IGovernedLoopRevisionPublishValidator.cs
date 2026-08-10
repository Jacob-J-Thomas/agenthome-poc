using EmbodySense.Core.Application.Loops.Revisions.Models;

namespace EmbodySense.Core.Application.Loops.Revisions;

/// <summary>Produces current server-owned validation evidence for one exact immutable publication candidate.</summary>
public interface IGovernedLoopRevisionPublishValidator
{
    /// <summary>Validates the exact artifact selected by a publish or rollback request.</summary>
    /// <param name="request">The exact canonical request and artifact binding.</param>
    /// <param name="cancellationToken">The cancellation token used while validating.</param>
    /// <returns>A bounded decision and validation evidence digest bound to the exact candidate.</returns>
    Task<GovernedLoopRevisionPublishValidation> ValidateAsync(
        GovernedLoopRevisionPublishValidationRequest request,
        CancellationToken cancellationToken = default);
}
