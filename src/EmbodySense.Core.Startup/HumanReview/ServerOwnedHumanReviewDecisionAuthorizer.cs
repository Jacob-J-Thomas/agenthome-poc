using EmbodySense.Core.Application.HumanReview;
using ApplicationAuthorization = EmbodySense.Core.Application.HumanReview.Models.HumanReviewDecisionAuthorization;
using ApplicationAuthorizationRequest = EmbodySense.Core.Application.HumanReview.Models.HumanReviewDecisionAuthorizationRequest;
using EmbodySense.Core.Common.HumanReview;
using CommonDecisionKind = EmbodySense.Core.Common.HumanReview.Models.HumanReviewDecisionKind;
using StartupAuthorizationRequest = EmbodySense.Core.Startup.HumanReview.Models.HumanReviewDecisionAuthorizationRequest;
using EmbodySense.Core.Startup.HumanReview.Models;

namespace EmbodySense.Core.Startup.HumanReview;

/// <summary>Adapts a Startup-owned server authority source to the canonical Application authorizer port.</summary>
/// <remarks>A missing provider, provider exception, cancellation-safe ambiguity, or response that does not echo every exact
/// server binding is returned as an unavailable authorization. A bound denial is preserved as a bound unauthorized Application
/// response. The adapter never derives authority from caller input and never exposes the Application request to its provider.</remarks>
internal sealed class ServerOwnedHumanReviewDecisionAuthorizer : IHumanReviewDecisionAuthorizer
{
    private readonly IHumanReviewDecisionAuthorizationProvider? _provider;

    /// <summary>Initializes the adapter.</summary>
    /// <param name="provider">The server-owned provider, or null when this runtime has no reviewer authority.</param>
    internal ServerOwnedHumanReviewDecisionAuthorizer(IHumanReviewDecisionAuthorizationProvider? provider)
        => _provider = provider;

    /// <inheritdoc />
    public async Task<ApplicationAuthorization> AuthorizeAsync(ApplicationAuthorizationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var canonicalRequest = request.Request;
        var proposal = request.Proposal;
        if (_provider is null
            || canonicalRequest is null
            || proposal is null
            || !HumanReviewIdentifier.IsValid(canonicalRequest.RequestId)
            || !HumanReviewIdentifier.IsValid(request.DecisionOperationId)
            || !string.Equals(canonicalRequest.RequestHash, request.RequestHash, StringComparison.Ordinal)
            || !string.Equals(proposal.DecisionOperationId, request.DecisionOperationId, StringComparison.Ordinal)
            || !string.Equals(proposal.ProposalHash, request.ProposalHash, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(request.RequestHash)
            || string.IsNullOrWhiteSpace(request.ProposalHash)
            || !Enum.IsDefined(request.Proposal.Kind)
            || request.EvaluatedAtUtc == default
            || request.EvaluatedAtUtc.Offset != TimeSpan.Zero)
        {
            return null!;
        }

        var decisionKind = MapDecisionKind(proposal.Kind);
        if (decisionKind == HumanReviewDecisionKind.Unknown)
        {
            return null!;
        }

        StartupAuthorizationRequest providerRequest;
        HumanReviewDecisionAuthorizationResult? authorization;
        try
        {
            providerRequest = new StartupAuthorizationRequest(
                canonicalRequest.RequestId,
                request.RequestHash,
                decisionKind,
                request.DecisionOperationId,
                request.ProposalHash,
                request.EvaluatedAtUtc);
            authorization = await _provider.AuthorizeAsync(providerRequest, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null!;
        }

        if (!IsBound(providerRequest, authorization))
        {
            return null!;
        }

        if (authorization!.Status == HumanReviewDecisionAuthorizationStatus.Unavailable)
        {
            return null!;
        }

        if (authorization.Status == HumanReviewDecisionAuthorizationStatus.Denied)
        {
            return new ApplicationAuthorization(false, request.RequestHash, request.DecisionOperationId, request.ProposalHash, request.EvaluatedAtUtc, null, null, [], null);
        }

        if (authorization.Status != HumanReviewDecisionAuthorizationStatus.Ready)
        {
            return null!;
        }

        return new ApplicationAuthorization(true, request.RequestHash, request.DecisionOperationId, request.ProposalHash, request.EvaluatedAtUtc, authorization.ActorId, authorization.ReviewerRoleId, authorization.ScopeIds, authorization.CorrelationId);
    }

    private static bool IsBound(StartupAuthorizationRequest request, HumanReviewDecisionAuthorizationResult? authorization)
        => authorization is not null
            && authorization.RequestId == request.RequestId
            && string.Equals(authorization.RequestHash, request.RequestHash, StringComparison.Ordinal)
            && authorization.DecisionKind == request.DecisionKind
            && string.Equals(authorization.DecisionOperationId, request.DecisionOperationId, StringComparison.Ordinal)
            && string.Equals(authorization.ProposalHash, request.ProposalHash, StringComparison.Ordinal)
            && authorization.EvaluatedAtUtc == request.EvaluatedAtUtc;

    private static HumanReviewDecisionKind MapDecisionKind(CommonDecisionKind source)
        => source switch
        {
            CommonDecisionKind.Approve => HumanReviewDecisionKind.Approve,
            CommonDecisionKind.Reject => HumanReviewDecisionKind.Reject,
            CommonDecisionKind.Cancel => HumanReviewDecisionKind.Cancel,
            CommonDecisionKind.RequestInformation => HumanReviewDecisionKind.RequestInformation,
            _ => HumanReviewDecisionKind.Unknown
        };
}
