using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Application.Loops.GraphValidation;
using EmbodySense.Core.Common.HumanReview;
using EmbodySense.Core.Startup.HumanReview.Models;
using EmbodySense.Core.Startup.Workspace;

namespace EmbodySense.Core.Startup.HumanReview;

/// <summary>Resolves the local server-owned reviewer policy for one exact Human Review decision.</summary>
/// <remarks>
/// The policy accepts only the single canonical Application reviewer role and one structurally valid persisted eligibility
/// entry. It does not inspect caller-provided identity claims. Returned scope arrays are detached, and the correlation
/// identity is a deterministic lowercase SHA-256 digest of the exact request and server-owned policy facts.
/// </remarks>
public sealed class HumanReviewLocalDecisionAuthorizationPolicy
{
    /// <summary>Creates the local reviewer policy.</summary>
    public HumanReviewLocalDecisionAuthorizationPolicy()
    {
    }

    /// <summary>Authorizes one exact request under the server-owned local reviewer policy.</summary>
    /// <param name="request">The Startup request containing server-derived decision and eligibility facts.</param>
    /// <returns>A bound ready result when the request is canonical; otherwise a bound unavailable result.</returns>
    public HumanReviewDecisionAuthorizationResult Authorize(HumanReviewDecisionAuthorizationRequest? request)
    {
        if (!IsCanonicalRequest(request, out var scopes))
        {
            return Unavailable(request);
        }

        var canonicalRequest = request!;
        var correlationId = ComputeCorrelation(canonicalRequest, scopes);
        return new HumanReviewDecisionAuthorizationResult(
            HumanReviewDecisionAuthorizationStatus.Ready,
            canonicalRequest.RequestId,
            canonicalRequest.RequestHash,
            canonicalRequest.DecisionKind,
            canonicalRequest.DecisionOperationId,
            canonicalRequest.ProposalHash,
            canonicalRequest.EvaluatedAtUtc,
            WorkspaceActors.Web,
            GovernedLoopHumanReviewNodeCatalogContract.LocalReviewerRoleId,
            scopes,
            correlationId);
    }

    private static HumanReviewDecisionAuthorizationResult Unavailable(HumanReviewDecisionAuthorizationRequest? request)
        => new(
            HumanReviewDecisionAuthorizationStatus.Unavailable,
            request?.RequestId ?? string.Empty,
            request?.RequestHash ?? string.Empty,
            request?.DecisionKind ?? HumanReviewDecisionKind.Unknown,
            request?.DecisionOperationId ?? string.Empty,
            request?.ProposalHash ?? string.Empty,
            request?.EvaluatedAtUtc ?? default,
            null,
            null,
            ImmutableArray<string>.Empty,
            null);

    private static bool IsCanonicalRequest(HumanReviewDecisionAuthorizationRequest? request, out ImmutableArray<string> scopes)
    {
        scopes = default;
        if (request is null
            || !HumanReviewIdentifier.IsValid(request.RequestId)
            || !HumanReviewContractHash.IsSha256(request.RequestHash)
            || !Enum.IsDefined(request.DecisionKind)
            || request.DecisionKind == HumanReviewDecisionKind.Unknown
            || !HumanReviewIdentifier.IsValid(request.DecisionOperationId)
            || !HumanReviewContractHash.IsSha256(request.ProposalHash)
            || request.EvaluatedAtUtc == default
            || request.EvaluatedAtUtc.Offset != TimeSpan.Zero
            || request.EligibleReviewers.IsDefault
            || request.EligibleReviewers.Length != 1)
        {
            return false;
        }

        var eligibility = request.EligibleReviewers[0];
        if (eligibility is null
            || !string.Equals(eligibility.ReviewerRoleId, GovernedLoopHumanReviewNodeCatalogContract.LocalReviewerRoleId, StringComparison.Ordinal)
            || !TryCopyCanonicalScopes(eligibility.ScopeIds, out scopes))
        {
            scopes = default;
            return false;
        }

        return true;
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

    private static string ComputeCorrelation(HumanReviewDecisionAuthorizationRequest request, ImmutableArray<string> scopes)
    {
        var canonical = new StringBuilder(512);
        Append(canonical, "human-review-local-policy-v1");
        Append(canonical, request.RequestId);
        Append(canonical, request.RequestHash);
        Append(canonical, ((int)request.DecisionKind).ToString(CultureInfo.InvariantCulture));
        Append(canonical, request.DecisionOperationId);
        Append(canonical, request.ProposalHash);
        Append(canonical, request.EvaluatedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        Append(canonical, WorkspaceActors.Web);
        Append(canonical, GovernedLoopHumanReviewNodeCatalogContract.LocalReviewerRoleId);
        Append(canonical, scopes.Length.ToString(CultureInfo.InvariantCulture));
        foreach (var scope in scopes)
        {
            Append(canonical, scope);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();
    }

    private static void Append(StringBuilder canonical, string? value)
    {
        if (value is null)
        {
            canonical.Append("N;");
            return;
        }

        canonical.Append('S').Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append(';');
    }
}
