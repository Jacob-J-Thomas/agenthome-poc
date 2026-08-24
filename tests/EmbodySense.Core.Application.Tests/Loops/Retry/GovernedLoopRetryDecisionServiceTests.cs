using EmbodySense.Core.Application.Loops.Retry;
using EmbodySense.Core.Application.Loops.Retry.Models;
using EmbodySense.Core.Common.Loops.Execution.Retry;
using EmbodySense.Core.Common.Loops.Execution.Retry.Models;
using EmbodySense.Core.Common.Loops.Failures;
using EmbodySense.Core.Common.Loops.Failures.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Application.Tests.Loops.Retry;

public sealed class GovernedLoopRetryDecisionServiceTests
{
    private static readonly DateTimeOffset _startedAtUtc = DateTimeOffset.UnixEpoch.AddDays(1);

    [Fact]
    public void Affirmatively_safe_failure_produces_one_restart_stable_bounded_schedule()
    {
        var request = Request();

        var first = GovernedLoopRetryDecisionService.Evaluate(request);
        var replay = GovernedLoopRetryDecisionService.Evaluate(request);

        Assert.Equal(GovernedLoopRetryDecisionStatus.Schedule, first.Status);
        Assert.Equal(2, first.NextAttempt);
        Assert.NotNull(first.Series);
        Assert.Equal(first, replay);
        Assert.StartsWith("retry-", first.AttemptOperationId, StringComparison.Ordinal);
        Assert.InRange(first.EligibleAtUtc!.Value, request.EvaluatedAtUtc, request.EvaluatedAtUtc.AddSeconds(6));
    }

    [Fact]
    public void Ambiguous_or_not_retryable_failure_never_schedules()
    {
        var failure = Failure() with
        {
            FailureClass = GovernedLoopFailureClass.AmbiguousExternalOutcome,
            RetrySafety = GovernedLoopFailureRetrySafety.NotRetryable,
        };

        var decision = GovernedLoopRetryDecisionService.Evaluate(Request(failure: failure));

        Assert.Equal(GovernedLoopRetryDecisionStatus.NoRetry, decision.Status);
        Assert.Null(decision.AttemptOperationId);
    }

    [Fact]
    public void Unknown_hard_usage_ceiling_fails_closed_to_review()
    {
        var request = Request() with
        {
            Budget = new GovernedLoopRetryBudgetSnapshot(1, null, 0, 5, "USD", 1),
        };

        var decision = GovernedLoopRetryDecisionService.Evaluate(request);

        Assert.Equal(GovernedLoopRetryDecisionStatus.NeedsReview, decision.Status);
        Assert.Null(decision.NextAttempt);
    }

    [Fact]
    public void Cost_evidence_in_another_currency_is_rejected_instead_of_combined()
    {
        var decision = GovernedLoopRetryDecisionService.Evaluate(Request() with
        {
            Budget = new GovernedLoopRetryBudgetSnapshot(1, 10, 0, 5, "EUR", 1),
        });

        Assert.Equal(GovernedLoopRetryDecisionStatus.Invalid, decision.Status);
        Assert.Null(decision.NextAttempt);
    }

    [Fact]
    public void Attempt_resource_deadline_and_current_posture_bounds_are_terminally_enforced()
    {
        var attemptExhausted = GovernedLoopRetryDecisionService.Evaluate(Request(currentAttempt: 4));
        var resourceExhausted = GovernedLoopRetryDecisionService.Evaluate(Request() with
        {
            Budget = new GovernedLoopRetryBudgetSnapshot(1, 10_000, 0, 5, "USD", 1),
        });
        var deadlineExhausted = GovernedLoopRetryDecisionService.Evaluate(Request() with
        {
            EnclosingDeadlineUtc = _startedAtUtc.AddSeconds(10),
        });
        var revoked = GovernedLoopRetryDecisionService.Evaluate(Request() with { CurrentAuthorityEligible = false });

        Assert.Equal(GovernedLoopRetryDecisionStatus.Exhausted, attemptExhausted.Status);
        Assert.Equal(GovernedLoopRetryDecisionStatus.Exhausted, resourceExhausted.Status);
        Assert.Equal(GovernedLoopRetryDecisionStatus.Exhausted, deadlineExhausted.Status);
        Assert.Equal(GovernedLoopRetryDecisionStatus.NoRetry, revoked.Status);
    }

    [Fact]
    public void Existing_series_must_match_exact_run_revision_node_visit_and_policy()
    {
        var first = GovernedLoopRetryDecisionService.Evaluate(Request());
        var substituted = first.Series! with { RunId = "other-run" };

        var decision = GovernedLoopRetryDecisionService.Evaluate(Request() with { ExistingSeries = substituted });

        Assert.Equal(GovernedLoopRetryDecisionStatus.Conflict, decision.Status);
        Assert.Equal("retry-series-substituted", decision.Detail);
    }

    [Fact]
    public void Immediate_pause_cancel_and_review_postures_return_distinct_fail_closed_decisions()
    {
        var paused = GovernedLoopRetryDecisionService.Evaluate(Request() with { LifecyclePosture = GovernedLoopRetryLifecyclePosture.Paused });
        var cancelled = GovernedLoopRetryDecisionService.Evaluate(Request() with { LifecyclePosture = GovernedLoopRetryLifecyclePosture.Cancelled });
        var review = GovernedLoopRetryDecisionService.Evaluate(Request() with { LifecyclePosture = GovernedLoopRetryLifecyclePosture.ReviewBlocked });

        Assert.Equal(GovernedLoopRetryDecisionStatus.Paused, paused.Status);
        Assert.Equal(GovernedLoopRetryDecisionStatus.Cancelled, cancelled.Status);
        Assert.Equal(GovernedLoopRetryDecisionStatus.NeedsReview, review.Status);
        Assert.All([paused, cancelled, review], decision => Assert.Null(decision.AttemptOperationId));
    }

    [Fact]
    public void Zero_delay_policy_returns_due_without_bypassing_durable_publication()
    {
        var policy = Policy();
        var noDelay = GovernedLoopRetryContract.CreatePolicy(
            policy.PolicyId,
            policy.NodeId,
            policy.FailureClasses,
            policy.ServerCodes,
            policy.MaximumAttempts,
            policy.PerAttemptTimeoutMilliseconds,
            policy.MaximumElapsedMilliseconds,
            GovernedLoopRetryBackoffStrategy.None,
            0,
            0,
            GovernedLoopRetryJitterStrategy.None,
            0,
            policy.MaximumTokens,
            policy.MaximumToolCalls,
            policy.MaximumCostMicrounits,
            policy.MaximumCostCurrency,
            policy.MaximumResourceUnits);

        var decision = GovernedLoopRetryDecisionService.Evaluate(Request() with { Policy = noDelay });

        Assert.Equal(GovernedLoopRetryDecisionStatus.Due, decision.Status);
        Assert.Equal(Request().EvaluatedAtUtc, decision.EligibleAtUtc);
        Assert.NotNull(decision.AttemptOperationId);
    }

    private static GovernedLoopRetryEvaluationRequest Request(
        int currentAttempt = 1,
        GovernedLoopFailureEvidence? failure = null)
        => new(
            Policy(),
            failure ?? Failure(attempt: currentAttempt),
            null,
            currentAttempt,
            new GovernedLoopRetryBudgetSnapshot(currentAttempt, 10, 0, 5, "USD", 1),
            _startedAtUtc,
            _startedAtUtc.AddSeconds(currentAttempt + 1),
            null,
            true,
            true,
            true);

    private static GovernedLoopRetryPolicy Policy()
        => GovernedLoopRetryContract.CreatePolicy(
            "policy-1",
            "node-1",
            [GovernedLoopFailureClass.RetryableNoEffect],
            ["retryable-no-effect"],
            4,
            10_000,
            60_000,
            GovernedLoopRetryBackoffStrategy.Exponential,
            1_000,
            5_000,
            GovernedLoopRetryJitterStrategy.DeterministicBounded,
            25,
            maximumTokens: 10_000,
            maximumToolCalls: 8,
            maximumCostMicrounits: 1_000_000,
            maximumCostCurrency: "USD",
            maximumResourceUnits: 8);

    private static GovernedLoopFailureEvidence Failure(int attempt = 1)
        => GovernedLoopFailureEvidenceContract.Create(
            "failure-evidence-" + attempt,
            $"workspace-sha256:{new string('e', 64)}",
            "run-1",
            GovernedLoopRevisionReference.Create(1, "graph-1", "revision-1", new string('f', 64)),
            1,
            0,
            1,
            "node-1",
            attempt,
            GovernedLoopFailureClass.RetryableNoEffect,
            "retryable-no-effect",
            GovernedLoopFailureSource.Provider,
            GovernedLoopFailureEffectCertainty.EffectProvedAbsent,
            GovernedLoopFailureAuthorityPosture.Current,
            GovernedLoopFailureHumanPosture.None,
            GovernedLoopFailureRetrySafety.RetryableWithExactIntent,
            GovernedLoopFailureSeverity.Error,
            500,
            [new GovernedLoopFailureEvidenceReference("provider-evidence", new string('a', 64))],
            "bounded failure",
            _startedAtUtc.AddSeconds(attempt));
}
