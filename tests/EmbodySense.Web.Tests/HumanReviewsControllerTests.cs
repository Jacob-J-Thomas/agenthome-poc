using EmbodySense.Core.Startup.HumanReview.Models;
using EmbodySense.Web.Controllers;
using EmbodySense.Web.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace EmbodySense.Web.Tests;

public sealed class HumanReviewsControllerTests
{
    [Fact]
    public async Task List_projects_bounds_and_all_closed_statuses()
    {
        var runtime = new HumanReviewControllerTestRuntime();
        var controller = CreateController(runtime, new HumanReviewControllerTestNotifier());

        Assert.Equal(StatusCodes.Status400BadRequest, (await controller.List(0)).ResultStatusCode());
        Assert.Equal(StatusCodes.Status400BadRequest, (await controller.List(51)).ResultStatusCode());

        foreach (var (status, expected) in new[]
        {
            (HumanReviewPageStatus.Ready, StatusCodes.Status200OK),
            (HumanReviewPageStatus.Invalid, StatusCodes.Status400BadRequest),
            (HumanReviewPageStatus.Ambiguous, StatusCodes.Status409Conflict),
            (HumanReviewPageStatus.Corrupt, StatusCodes.Status409Conflict),
            (HumanReviewPageStatus.Unknown, StatusCodes.Status503ServiceUnavailable),
            (HumanReviewPageStatus.Unavailable, StatusCodes.Status503ServiceUnavailable)
        })
        {
            runtime.PageResponse = new HumanReviewPage(status, [], null);
            Assert.Equal(expected, (await controller.List()).ResultStatusCode());
        }

        Assert.Equal(6, runtime.ListCalls);
    }

    [Fact]
    public async Task Reads_project_all_statuses_and_nested_effect_failures_without_dropping_projection()
    {
        var runtime = new HumanReviewControllerTestRuntime();
        var controller = CreateController(runtime, new HumanReviewControllerTestNotifier());
        foreach (var (status, expected) in new[]
        {
            (HumanReviewReadStatus.Ready, StatusCodes.Status200OK),
            (HumanReviewReadStatus.Invalid, StatusCodes.Status400BadRequest),
            (HumanReviewReadStatus.NotFound, StatusCodes.Status404NotFound),
            (HumanReviewReadStatus.Corrupt, StatusCodes.Status409Conflict),
            (HumanReviewReadStatus.Unknown, StatusCodes.Status503ServiceUnavailable),
            (HumanReviewReadStatus.Unavailable, StatusCodes.Status503ServiceUnavailable)
        })
        {
            runtime.ReadResponse = new HumanReviewReadResult(status, status == HumanReviewReadStatus.Ready ? Detail(HumanReviewEffectEvidenceStatus.ExactNotStarted) : null);
            runtime.EvidenceResponse = new HumanReviewEvidenceReadResult(MapEvidenceStatus(status), [], status == HumanReviewReadStatus.Ready ? Effect(HumanReviewEffectEvidenceStatus.ExactNotStarted) : null);
            runtime.PostureResponse = new HumanReviewRuntimePostureReadResult(status, null);
            Assert.Equal(expected, (await controller.Read("run-1")).ResultStatusCode());
            Assert.Equal(expected, (await controller.ReadEvidence("run-1")).ResultStatusCode());
            Assert.Equal(expected, (await controller.ReadPosture("run-1")).ResultStatusCode());
        }

        foreach (var status in new[]
        {
            HumanReviewEffectEvidenceStatus.Invalid,
            HumanReviewEffectEvidenceStatus.Ambiguous,
            HumanReviewEffectEvidenceStatus.Corrupt,
            HumanReviewEffectEvidenceStatus.Stale
        })
        {
            runtime.ReadResponse = new HumanReviewReadResult(HumanReviewReadStatus.Ready, Detail(status));
            runtime.EvidenceResponse = new HumanReviewEvidenceReadResult(HumanReviewEvidenceReadStatus.Ready, [], Effect(status));
            Assert.Equal(StatusCodes.Status409Conflict, (await controller.Read("run-1")).ResultStatusCode());
            Assert.Equal(StatusCodes.Status409Conflict, (await controller.ReadEvidence("run-1")).ResultStatusCode());
            Assert.NotNull((await controller.Read("run-1")).ObjectValue<HumanReviewReadResult>()?.Detail);
        }

        runtime.ReadResponse = new HumanReviewReadResult(HumanReviewReadStatus.Ready, Detail(HumanReviewEffectEvidenceStatus.Unavailable));
        runtime.EvidenceResponse = new HumanReviewEvidenceReadResult(HumanReviewEvidenceReadStatus.Ready, [], Effect(HumanReviewEffectEvidenceStatus.Unavailable));
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, (await controller.Read("run-1")).ResultStatusCode());
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, (await controller.ReadEvidence("run-1")).ResultStatusCode());
    }

    [Fact]
    public async Task Decisions_enforce_route_body_semantics_map_statuses_and_notify_only_new_acceptance()
    {
        var runtime = new HumanReviewControllerTestRuntime();
        var notifier = new HumanReviewControllerTestNotifier();
        var controller = CreateController(runtime, notifier);
        var valid = new WebHumanReviewDecisionRequest(3, "operation-1", null);

        Assert.Equal(StatusCodes.Status400BadRequest, (await controller.Approve("run-1", valid with { Detail = "not-allowed" })).ResultStatusCode());
        Assert.Equal(StatusCodes.Status400BadRequest, (await controller.Reject("run-1", valid with { Detail = string.Empty })).ResultStatusCode());
        Assert.Equal(StatusCodes.Status400BadRequest, (await controller.Cancel("run-1", valid with { Detail = "not-allowed" })).ResultStatusCode());
        Assert.Equal(StatusCodes.Status400BadRequest, (await controller.RequestInformation("run-1", valid)).ResultStatusCode());
        Assert.Equal(0, runtime.DecisionCalls);

        runtime.DecisionResponse = new HumanReviewDecisionResult(HumanReviewDecisionStatus.Accepted, "operation-1", null);
        Assert.Equal(StatusCodes.Status200OK, (await controller.Approve("run-1", valid)).ResultStatusCode());
        Assert.Single(notifier.Notifications);
        Assert.Equal("run-1", notifier.Notifications[0].RunId);
        Assert.Equal(HumanReviewDecisionKind.Approve, runtime.LastDecision?.Kind);

        foreach (var (status, expected) in new[]
        {
            (HumanReviewDecisionStatus.Accepted, StatusCodes.Status200OK),
            (HumanReviewDecisionStatus.InformationRequested, StatusCodes.Status200OK),
            (HumanReviewDecisionStatus.Replayed, StatusCodes.Status200OK),
            (HumanReviewDecisionStatus.Invalid, StatusCodes.Status400BadRequest),
            (HumanReviewDecisionStatus.NotFound, StatusCodes.Status404NotFound),
            (HumanReviewDecisionStatus.Denied, StatusCodes.Status403Forbidden),
            (HumanReviewDecisionStatus.Conflict, StatusCodes.Status409Conflict),
            (HumanReviewDecisionStatus.Expired, StatusCodes.Status409Conflict),
            (HumanReviewDecisionStatus.LimitExceeded, StatusCodes.Status409Conflict),
            (HumanReviewDecisionStatus.Unknown, StatusCodes.Status503ServiceUnavailable),
            (HumanReviewDecisionStatus.Unavailable, StatusCodes.Status503ServiceUnavailable)
        })
        {
            runtime.DecisionResponse = new HumanReviewDecisionResult(status, "operation-1", null);
            var request = valid with { Detail = "need context" };
            var result = await controller.RequestInformation("run-1", request);
            Assert.Equal(expected, result.ResultStatusCode());
        }

        Assert.Equal(3, notifier.Notifications.Count);
        Assert.Equal(12, runtime.DecisionCalls);
    }

    [Fact]
    public async Task Decision_notification_failure_does_not_change_durable_result_and_unknown_failures_are_safe()
    {
        var runtime = new HumanReviewControllerTestRuntime
        {
            DecisionResponse = new HumanReviewDecisionResult(HumanReviewDecisionStatus.InformationRequested, "operation-1", null)
        };
        var notifier = new HumanReviewControllerTestNotifier { Exception = new InvalidOperationException("private notifier detail") };
        var controller = CreateController(runtime, notifier);
        var response = await controller.RequestInformation("run-1", new WebHumanReviewDecisionRequest(1, "operation-1", "need context"));

        Assert.Equal(StatusCodes.Status200OK, response.ResultStatusCode());
        Assert.Single(notifier.Notifications);

        runtime.DecisionException = new InvalidOperationException("private runtime detail");
        var unavailable = await controller.Approve("run-1", new WebHumanReviewDecisionRequest(1, "operation-2", null));
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, unavailable.ResultStatusCode());
        Assert.DoesNotContain("private", unavailable.RawObjectValue()?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Uninitialized_workspace_returns_established_conflict_before_runtime_operations()
    {
        var runtime = new HumanReviewControllerTestRuntime { IsWorkspaceInitialized = false };
        var controller = CreateController(runtime, new HumanReviewControllerTestNotifier());

        Assert.Equal(StatusCodes.Status409Conflict, (await controller.List(1)).ResultStatusCode());
        Assert.Equal(StatusCodes.Status409Conflict, (await controller.Read("run-1")).ResultStatusCode());
        Assert.Equal(StatusCodes.Status409Conflict, (await controller.ReadEvidence("run-1")).ResultStatusCode());
        Assert.Equal(StatusCodes.Status409Conflict, (await controller.ReadPosture("run-1")).ResultStatusCode());
        Assert.Equal(StatusCodes.Status409Conflict, (await controller.Approve("run-1", new WebHumanReviewDecisionRequest(1, "operation-1", null))).ResultStatusCode());
        Assert.Equal(0, runtime.ListCalls + runtime.ReadCalls + runtime.EvidenceCalls + runtime.PostureCalls + runtime.DecisionCalls);
    }

    private static HumanReviewDetail Detail(HumanReviewEffectEvidenceStatus status)
    {
        var summary = new HumanReviewSummary("run-1", "request-1", "hash-1", HumanReviewPurpose.Continuation, [], HumanReviewLifecycleStatus.Pending, CustomLoopRunStatus.Paused, GovernedLoopFrontierStatus.ReviewBlocked, 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddHours(1));
        var runtime = new HumanReviewRuntimePosture(CustomLoopRunStatus.Paused, GovernedLoopFrontierStatus.ReviewBlocked, HumanReviewLifecycleStatus.Pending, HumanReviewContinuationStatus.Reserved, 1, 0, 0, 0, DateTimeOffset.UnixEpoch);
        return new HumanReviewDetail(summary, [], [], [], runtime, Effect(status));
    }

    private static HumanReviewEffectEvidence Effect(HumanReviewEffectEvidenceStatus status)
        => new(status, null, null, null, "attempt-1");

    private static HumanReviewEvidenceReadStatus MapEvidenceStatus(HumanReviewReadStatus status)
        => status switch
        {
            HumanReviewReadStatus.Ready => HumanReviewEvidenceReadStatus.Ready,
            HumanReviewReadStatus.Invalid => HumanReviewEvidenceReadStatus.Invalid,
            HumanReviewReadStatus.NotFound => HumanReviewEvidenceReadStatus.NotFound,
            HumanReviewReadStatus.Corrupt => HumanReviewEvidenceReadStatus.Corrupt,
            _ => HumanReviewEvidenceReadStatus.Unavailable,
        };

    private static HumanReviewsController CreateController(HumanReviewControllerTestRuntime runtime, HumanReviewControllerTestNotifier notifier)
        => new(runtime, notifier, NullLogger<HumanReviewsController>.Instance);
}
