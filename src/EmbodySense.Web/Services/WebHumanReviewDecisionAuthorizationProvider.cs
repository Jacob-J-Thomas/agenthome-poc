using EmbodySense.Core.Startup.HumanReview;
using EmbodySense.Core.Startup.HumanReview.Models;
using Microsoft.AspNetCore.Http;

namespace EmbodySense.Web.Services;

/// <summary>Adapts the current authenticated Web request to the server-owned Human Review authority policy.</summary>
/// <remarks>
/// The current <see cref="HttpContext"/> is read for each call and is never retained. Authentication claims, connection
/// identifiers, cookies, headers, and browser payloads are not authority inputs; the Startup policy supplies the canonical
/// actor, role, scopes, and correlation identity.
/// </remarks>
public sealed class WebHumanReviewDecisionAuthorizationProvider : IHumanReviewDecisionAuthorizationProvider
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly HumanReviewLocalDecisionAuthorizationPolicy _policy;

    /// <summary>Initializes the Web authority provider.</summary>
    /// <param name="httpContextAccessor">The accessor used to inspect only the current request authentication state.</param>
    /// <param name="policy">The Startup-owned local reviewer policy.</param>
    /// <exception cref="ArgumentNullException">Thrown when either dependency is null.</exception>
    public WebHumanReviewDecisionAuthorizationProvider(IHttpContextAccessor httpContextAccessor, HumanReviewLocalDecisionAuthorizationPolicy policy)
    {
        _httpContextAccessor = httpContextAccessor ?? throw new ArgumentNullException(nameof(httpContextAccessor));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }

    /// <inheritdoc />
    public Task<HumanReviewDecisionAuthorizationResult?> AuthorizeAsync(HumanReviewDecisionAuthorizationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        HttpContext? context;
        try
        {
            context = _httpContextAccessor.HttpContext;
        }
        catch
        {
            return Task.FromResult<HumanReviewDecisionAuthorizationResult?>(Closed(request, HumanReviewDecisionAuthorizationStatus.Unavailable));
        }

        if (context is null)
        {
            return Task.FromResult<HumanReviewDecisionAuthorizationResult?>(Closed(request, HumanReviewDecisionAuthorizationStatus.Unavailable));
        }

        if (context.RequestAborted.IsCancellationRequested)
        {
            return Task.FromResult<HumanReviewDecisionAuthorizationResult?>(Closed(request, HumanReviewDecisionAuthorizationStatus.Unavailable));
        }

        var identities = context.User?.Identities?.ToArray();
        if (identities is null || identities.Length == 0)
        {
            return Task.FromResult<HumanReviewDecisionAuthorizationResult?>(Closed(request, HumanReviewDecisionAuthorizationStatus.Unavailable));
        }

        if (identities.Length != 1 || !identities[0].IsAuthenticated)
        {
            return Task.FromResult<HumanReviewDecisionAuthorizationResult?>(Closed(request, HumanReviewDecisionAuthorizationStatus.Unavailable));
        }

        if (!string.Equals(identities[0].AuthenticationType, WebSessionAuthenticationDefaults.Scheme, StringComparison.Ordinal))
        {
            return Task.FromResult<HumanReviewDecisionAuthorizationResult?>(Closed(request, HumanReviewDecisionAuthorizationStatus.Denied));
        }

        return Task.FromResult<HumanReviewDecisionAuthorizationResult?>(_policy.Authorize(request));
    }

    private static HumanReviewDecisionAuthorizationResult Closed(HumanReviewDecisionAuthorizationRequest request, HumanReviewDecisionAuthorizationStatus status)
        => new(status, request.RequestId, request.RequestHash, request.DecisionKind, request.DecisionOperationId, request.ProposalHash, request.EvaluatedAtUtc, null, null, [], null);
}
