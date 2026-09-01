using System.Collections.Immutable;
using ApplicationAuthorizationRequest = EmbodySense.Core.Application.HumanReview.Models.HumanReviewDecisionAuthorizationRequest;
using CommonDecisionKind = EmbodySense.Core.Common.HumanReview.Models.HumanReviewDecisionKind;
using EmbodySense.Core.Startup.HumanReview;
using StartupAuthorizationStatus = EmbodySense.Core.Startup.HumanReview.Models.HumanReviewDecisionAuthorizationStatus;
using StartupDecisionKind = EmbodySense.Core.Startup.HumanReview.Models.HumanReviewDecisionKind;
using EmbodySense.Core.Startup.HumanReview.Models;
using StartupAuthorizationRequest = EmbodySense.Core.Startup.HumanReview.Models.HumanReviewDecisionAuthorizationRequest;
using StartupAuthorizationResult = EmbodySense.Core.Startup.HumanReview.Models.HumanReviewDecisionAuthorizationResult;

namespace EmbodySense.Core.Startup.Tests.HumanReview;

internal sealed class HumanReviewRecordingAuthorizationProvider(
    ApplicationAuthorizationRequest expected,
    StartupAuthorizationStatus status = StartupAuthorizationStatus.Ready,
    bool mismatch = false) : IHumanReviewDecisionAuthorizationProvider
{
    public StartupAuthorizationRequest? Request { get; private set; }

    public Task<StartupAuthorizationResult?> AuthorizeAsync(StartupAuthorizationRequest request, CancellationToken cancellationToken = default)
    {
        Request = request;
        return Task.FromResult<StartupAuthorizationResult?>(new StartupAuthorizationResult(
            status,
            mismatch ? "wrong-request-id" : expected.Request.RequestId,
            mismatch ? "wrong-request" : expected.RequestHash,
            mismatch ? StartupDecisionKind.Cancel : MapDecisionKind(expected.Proposal.Kind),
            expected.DecisionOperationId,
            expected.ProposalHash,
            expected.EvaluatedAtUtc,
            "actor",
            request.EligibleReviewers[0].ReviewerRoleId,
            request.EligibleReviewers[0].ScopeIds,
            "correlation"));
    }

    private static StartupDecisionKind MapDecisionKind(CommonDecisionKind kind)
        => kind switch
        {
            CommonDecisionKind.Approve => StartupDecisionKind.Approve,
            CommonDecisionKind.Reject => StartupDecisionKind.Reject,
            CommonDecisionKind.Cancel => StartupDecisionKind.Cancel,
            CommonDecisionKind.RequestInformation => StartupDecisionKind.RequestInformation,
            _ => StartupDecisionKind.Unknown
        };
}
