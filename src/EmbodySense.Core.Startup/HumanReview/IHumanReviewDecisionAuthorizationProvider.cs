using EmbodySense.Core.Startup.HumanReview.Models;

namespace EmbodySense.Core.Startup.HumanReview;

/// <summary>Supplies server-owned authorization for one exact Human Review decision evaluation.</summary>
/// <remarks>The provider is called only with facts composed by the canonical decision service. It must obtain actor, role, scope,
/// and authentication state from the hosting boundary; no interface payload is an authority source.</remarks>
public interface IHumanReviewDecisionAuthorizationProvider
{
    /// <summary>Authorizes the exact decision request or returns a fail-closed result.</summary>
    /// <param name="request">The exact server-derived request and proposal binding, without Application models or caller authority.</param>
    /// <param name="cancellationToken">Cancels the bounded authorization read.</param>
    /// <returns>An exact authorization response, or <see langword="null"/> when the provider is unavailable or ambiguous.</returns>
    Task<HumanReviewDecisionAuthorizationResult?> AuthorizeAsync(HumanReviewDecisionAuthorizationRequest request, CancellationToken cancellationToken = default);
}
