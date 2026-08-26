using EmbodySense.Core.Application.HumanReview.Models;

namespace EmbodySense.Core.Application.HumanReview;

/// <summary>Authenticates the caller and resolves its current exact reviewer role and scopes for one bound decision evaluation.</summary>
public interface IHumanReviewDecisionAuthorizer
{
    /// <summary>Authorizes the current caller before any replay result is disclosed.</summary>
    /// <param name="request">The complete server-derived request, proposal, hash, operation, and trusted-time binding.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A response that exactly echoes <paramref name="request"/>, or a denied/unavailable outcome.</returns>
    Task<HumanReviewDecisionAuthorization> AuthorizeAsync(HumanReviewDecisionAuthorizationRequest request, CancellationToken cancellationToken = default);
}
