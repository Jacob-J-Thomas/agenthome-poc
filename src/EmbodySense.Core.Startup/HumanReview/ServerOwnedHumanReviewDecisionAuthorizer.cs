using System.Collections.Immutable;
using EmbodySense.Core.Application.HumanReview;
using ApplicationAuthorization = EmbodySense.Core.Application.HumanReview.Models.HumanReviewDecisionAuthorization;
using ApplicationAuthorizationRequest = EmbodySense.Core.Application.HumanReview.Models.HumanReviewDecisionAuthorizationRequest;
using EmbodySense.Core.Common.HumanReview;
using CommonDecisionKind = EmbodySense.Core.Common.HumanReview.Models.HumanReviewDecisionKind;
using CommonReviewerScope = EmbodySense.Core.Common.HumanReview.Models.HumanReviewReviewerScope;
using StartupAuthorizationRequest = EmbodySense.Core.Startup.HumanReview.Models.HumanReviewDecisionAuthorizationRequest;
using StartupEligibility = EmbodySense.Core.Startup.HumanReview.Models.HumanReviewDecisionAuthorizationEligibility;
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

        if (!TryProjectEligibility(canonicalRequest.EligibleReviewers, out var eligibleReviewers))
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
                request.EvaluatedAtUtc,
                eligibleReviewers);
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

        if (!TryValidateReadyAuthorization(authorization, providerRequest.EligibleReviewers, out var scopeIds))
        {
            return null!;
        }

        return new ApplicationAuthorization(true, request.RequestHash, request.DecisionOperationId, request.ProposalHash, request.EvaluatedAtUtc, authorization.ActorId, authorization.ReviewerRoleId, scopeIds, authorization.CorrelationId);
    }

    private static bool IsBound(StartupAuthorizationRequest request, HumanReviewDecisionAuthorizationResult? authorization)
        => authorization is not null
            && string.Equals(authorization.RequestId, request.RequestId, StringComparison.Ordinal)
            && string.Equals(authorization.RequestHash, request.RequestHash, StringComparison.Ordinal)
            && authorization.DecisionKind == request.DecisionKind
            && string.Equals(authorization.DecisionOperationId, request.DecisionOperationId, StringComparison.Ordinal)
            && string.Equals(authorization.ProposalHash, request.ProposalHash, StringComparison.Ordinal)
            && authorization.EvaluatedAtUtc.EqualsExact(request.EvaluatedAtUtc);

    private static bool TryProjectEligibility(
        ImmutableArray<CommonReviewerScope> source,
        out ImmutableArray<StartupEligibility> projection)
    {
        projection = default;
        if (source.IsDefault || source.Length is < 1 or > HumanReviewContractLimits.MaxEligibleReviewers)
        {
            return false;
        }

        var projected = new StartupEligibility[source.Length];
        string? previousRole = null;
        for (var index = 0; index < source.Length; index++)
        {
            var reviewer = source[index];
            if (reviewer is null
                || !HumanReviewIdentifier.IsValid(reviewer.ReviewerRoleId)
                || previousRole is not null && string.CompareOrdinal(previousRole, reviewer.ReviewerRoleId) >= 0
                || !TryCopyCanonicalScopes(reviewer.ScopeIds, out var scopeIds))
            {
                return false;
            }

            projected[index] = new StartupEligibility(reviewer.ReviewerRoleId, scopeIds);
            previousRole = reviewer.ReviewerRoleId;
        }

        projection = ImmutableArray.CreateRange(projected);
        return true;
    }

    private static bool TryValidateReadyAuthorization(
        HumanReviewDecisionAuthorizationResult authorization,
        ImmutableArray<StartupEligibility> eligibleReviewers,
        out ImmutableArray<string> scopeIds)
    {
        scopeIds = default;
        if (!HumanReviewIdentifier.IsValid(authorization.ActorId)
            || !HumanReviewIdentifier.IsValid(authorization.ReviewerRoleId)
            || !HumanReviewIdentifier.IsValid(authorization.CorrelationId)
            || !TryCopyCanonicalScopes(authorization.ScopeIds, out scopeIds))
        {
            return false;
        }

        var candidateScopeIds = scopeIds;
        return eligibleReviewers.Any(item => string.Equals(item.ReviewerRoleId, authorization.ReviewerRoleId, StringComparison.Ordinal)
            && item.ScopeIds.SequenceEqual(candidateScopeIds, StringComparer.Ordinal));
    }

    private static bool TryCopyCanonicalScopes(ImmutableArray<string> source, out ImmutableArray<string> copy)
    {
        copy = default;
        if (source.IsDefault || source.Length is < 1 or > HumanReviewContractLimits.MaxScopesPerReviewer)
        {
            return false;
        }

        var values = new string[source.Length];
        for (var index = 0; index < source.Length; index++)
        {
            var value = source[index];
            if (!HumanReviewIdentifier.IsValid(value)
                || index > 0 && string.CompareOrdinal(values[index - 1], value) >= 0)
            {
                return false;
            }

            values[index] = value;
        }

        copy = ImmutableArray.CreateRange(values);
        return true;
    }

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
