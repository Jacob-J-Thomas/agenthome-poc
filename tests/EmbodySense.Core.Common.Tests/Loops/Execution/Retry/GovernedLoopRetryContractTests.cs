using EmbodySense.Core.Common.Loops.Execution.Retry;
using EmbodySense.Core.Common.Loops.Execution.Retry.Models;
using EmbodySense.Core.Common.Loops.Failures;
using EmbodySense.Core.Common.Loops.Failures.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using System.Globalization;

namespace EmbodySense.Core.Common.Tests.Loops.Execution.Retry;

public sealed class GovernedLoopRetryContractTests
{
    [Fact]
    public void Policy_is_canonical_hash_bound_and_rejects_unsafe_or_overbound_shapes()
    {
        var policy = Policy();

        Assert.True(GovernedLoopRetryContract.IsValid(policy));
        Assert.Equal(64, policy.ContentHash.Length);
        Assert.False(GovernedLoopRetryContract.IsValid(policy with { MaximumAttempts = GovernedLoopRetryContractLimits.MaximumAttempts + 1 }));
        Assert.False(GovernedLoopRetryContract.IsValid(policy with { FailureClasses = [GovernedLoopFailureClass.AmbiguousExternalOutcome] }));
        Assert.False(GovernedLoopRetryContract.IsValid(policy with { ServerCodes = ["token=private"] }));
        Assert.False(GovernedLoopRetryContract.IsValid(policy with { MaximumTokens = GovernedLoopRetryContractLimits.MaximumTokens + 1 }));
        Assert.False(GovernedLoopRetryContract.IsValid(policy with { MaximumCostCurrency = "EUR" }));
        Assert.False(GovernedLoopRetryContract.IsValid(policy with { MaximumCostCurrency = null }));
        Assert.False(GovernedLoopRetryContract.IsValid(policy with { ContentHash = new string('0', 64) }));
    }

    [Fact]
    public void Policy_rejects_noncanonical_sets_and_inconsistent_strategy_bounds()
    {
        var policy = Policy();

        Assert.False(GovernedLoopRetryContract.IsValid(policy with
        {
            FailureClasses = [GovernedLoopFailureClass.RetryableNoEffect, GovernedLoopFailureClass.DependencyUnavailableBeforeDispatch],
        }));
        Assert.False(GovernedLoopRetryContract.IsValid(policy with { ServerCodes = ["z-code", "a-code"] }));
        Assert.False(GovernedLoopRetryContract.IsValid(policy with { BackoffStrategy = GovernedLoopRetryBackoffStrategy.None }));
        Assert.False(GovernedLoopRetryContract.IsValid(policy with { JitterStrategy = GovernedLoopRetryJitterStrategy.None }));
    }

    [Fact]
    public void Fixed_and_exponential_backoff_are_deterministic_capped_and_restart_stable()
    {
        var fixedPolicy = Policy(
            backoff: GovernedLoopRetryBackoffStrategy.Fixed,
            initialDelayMilliseconds: 250,
            maximumDelayMilliseconds: 1_000,
            jitter: GovernedLoopRetryJitterStrategy.None,
            maximumJitterMilliseconds: 0);
        var exponential = Policy(
            backoff: GovernedLoopRetryBackoffStrategy.Exponential,
            initialDelayMilliseconds: 250,
            maximumDelayMilliseconds: 1_000,
            jitter: GovernedLoopRetryJitterStrategy.None,
            maximumJitterMilliseconds: 0);

        Assert.Equal(TimeSpan.FromMilliseconds(250), GovernedLoopRetryContract.ComputeDelay(fixedPolicy, new string('a', 64), 2));
        Assert.Equal(TimeSpan.FromMilliseconds(250), GovernedLoopRetryContract.ComputeDelay(fixedPolicy, new string('a', 64), 4));
        Assert.Equal(TimeSpan.FromMilliseconds(250), GovernedLoopRetryContract.ComputeDelay(exponential, new string('a', 64), 2));
        Assert.Equal(TimeSpan.FromMilliseconds(500), GovernedLoopRetryContract.ComputeDelay(exponential, new string('a', 64), 3));
        Assert.Equal(TimeSpan.FromMilliseconds(1_000), GovernedLoopRetryContract.ComputeDelay(exponential, new string('a', 64), 4));
        Assert.Equal(
            GovernedLoopRetryContract.ComputeDelay(Policy(), new string('b', 64), 3),
            GovernedLoopRetryContract.ComputeDelay(Policy(), new string('b', 64), 3));
    }

    [Fact]
    public void No_delay_and_hash_jitter_are_culture_independent_and_capped()
    {
        var none = Policy(
            backoff: GovernedLoopRetryBackoffStrategy.None,
            initialDelayMilliseconds: 0,
            maximumDelayMilliseconds: 0,
            jitter: GovernedLoopRetryJitterStrategy.None,
            maximumJitterMilliseconds: 0);
        var jittered = Policy(
            backoff: GovernedLoopRetryBackoffStrategy.Fixed,
            initialDelayMilliseconds: 990,
            maximumDelayMilliseconds: 1_000,
            jitter: GovernedLoopRetryJitterStrategy.DeterministicBounded,
            maximumJitterMilliseconds: 500);
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr-TR");
            var turkish = GovernedLoopRetryContract.ComputeDelay(jittered, new string('c', 64), 2);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ja-JP");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ja-JP");
            var japanese = GovernedLoopRetryContract.ComputeDelay(jittered, new string('c', 64), 2);

            Assert.Equal(TimeSpan.Zero, GovernedLoopRetryContract.ComputeDelay(none, new string('b', 64), 2));
            Assert.Equal(turkish, japanese);
            Assert.InRange(turkish, TimeSpan.FromMilliseconds(990), TimeSpan.FromMilliseconds(1_000));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    public void Series_binds_exact_revision_failure_policy_and_earliest_deadline()
    {
        var policy = Policy();
        var failure = Failure();
        var started = DateTimeOffset.UnixEpoch.AddDays(1);
        var enclosing = started.AddSeconds(30);

        var series = GovernedLoopRetryContract.CreateSeries(policy, failure, started, enclosing);

        Assert.True(GovernedLoopRetryContract.IsValid(series));
        Assert.Equal(enclosing, series.DeadlineUtc);
        Assert.Equal(failure.ContentHash, series.OriginatingFailureEvidenceHash);
        Assert.Equal(policy.ContentHash, series.PolicyHash);
        Assert.Throws<ArgumentException>(() => GovernedLoopRetryContract.CreateSeries(policy, failure with { RetrySafety = GovernedLoopFailureRetrySafety.Unknown }, started));
        Assert.Throws<ArgumentException>(() => GovernedLoopRetryContract.CreateSeries(policy, failure with { NodeId = "other-node" }, started));
        Assert.False(GovernedLoopRetryContract.IsValid(series with { SeriesId = null! }));
        Assert.False(GovernedLoopRetryContract.IsValid(series with { PolicyHash = null! }));
    }

    [Fact]
    public void State_requires_exact_attempt_wake_and_monotonic_transition_evidence()
    {
        var failure = Failure();
        var series = GovernedLoopRetryContract.CreateSeries(Policy(), failure, DateTimeOffset.UnixEpoch.AddDays(1));
        var retained = GovernedLoopRetryContract.CreateState(
            series,
            1,
            GovernedLoopRetryStateDisposition.FailureRetained,
            1,
            "attempt-1",
            null,
            null,
            new GovernedLoopRetryBudgetSnapshot(1, 10, 0, 5, "USD", 1),
            null,
            null,
            null,
            failure.EvidenceId,
            failure.ContentHash,
            series.StartedAtUtc);
        var scheduled = GovernedLoopRetryContract.CreateState(
            series,
            2,
            GovernedLoopRetryStateDisposition.Scheduled,
            1,
            "attempt-1",
            2,
            "retry-attempt-2",
            new GovernedLoopRetryBudgetSnapshot(1, 10, 0, 5, "USD", 1),
            series.StartedAtUtc.AddSeconds(1),
            new string('c', 64),
            new string('d', 64),
            failure.EvidenceId,
            failure.ContentHash,
            series.StartedAtUtc.AddMilliseconds(1));

        Assert.True(GovernedLoopRetryContract.IsValid(retained));
        Assert.True(GovernedLoopRetryContract.IsValid(scheduled));
        Assert.True(GovernedLoopRetryContract.IsValidTransition(retained, scheduled));
        Assert.False(GovernedLoopRetryContract.IsValidTransition(scheduled, retained));
        Assert.False(GovernedLoopRetryContract.IsValidTransition(retained, scheduled with { Budget = scheduled.Budget with { Tokens = null } }));
        Assert.False(GovernedLoopRetryContract.IsValidTransition(retained, scheduled with { Budget = scheduled.Budget with { CostCurrency = "EUR" } }));
        var due = GovernedLoopRetryContract.CreateState(series, 3, GovernedLoopRetryStateDisposition.Due, 1, "attempt-1", 2, "retry-attempt-2", scheduled.Budget, null, scheduled.WakeCheckpointId, scheduled.WakeCheckpointHash, failure.EvidenceId, failure.ContentHash, scheduled.RecordedAtUtc);
        var reservedBudget = scheduled.Budget with { Attempts = 2, ResourceUnits = 2 };
        var reserved = GovernedLoopRetryContract.CreateState(series, 4, GovernedLoopRetryStateDisposition.Reserved, 1, "attempt-1", 2, "retry-attempt-2", reservedBudget, null, scheduled.WakeCheckpointId, scheduled.WakeCheckpointHash, failure.EvidenceId, failure.ContentHash, scheduled.RecordedAtUtc);
        var dispatched = GovernedLoopRetryContract.CreateState(series, 5, GovernedLoopRetryStateDisposition.Dispatched, 1, "attempt-1", 2, "retry-attempt-2", reservedBudget, null, scheduled.WakeCheckpointId, scheduled.WakeCheckpointHash, failure.EvidenceId, failure.ContentHash, scheduled.RecordedAtUtc);
        var completed = GovernedLoopRetryContract.CreateState(series, 6, GovernedLoopRetryStateDisposition.AttemptCompleted, 2, "retry-attempt-2", null, null, reservedBudget, null, null, null, failure.EvidenceId, failure.ContentHash, scheduled.RecordedAtUtc);
        var jumped = GovernedLoopRetryContract.CreateState(series, 6, GovernedLoopRetryStateDisposition.AttemptCompleted, 3, "retry-attempt-3", null, null, reservedBudget with { Attempts = 3, ResourceUnits = 3 }, null, null, null, failure.EvidenceId, failure.ContentHash, scheduled.RecordedAtUtc);
        var substitutedDueOperation = GovernedLoopRetryContract.CreateState(
            series,
            3,
            GovernedLoopRetryStateDisposition.Due,
            1,
            "attempt-1",
            2,
            "substituted-attempt-2",
            scheduled.Budget,
            null,
            scheduled.WakeCheckpointId,
            scheduled.WakeCheckpointHash,
            failure.EvidenceId,
            failure.ContentHash,
            scheduled.RecordedAtUtc);
        var substitutedDueWake = GovernedLoopRetryContract.CreateState(
            series,
            3,
            GovernedLoopRetryStateDisposition.Due,
            1,
            "attempt-1",
            2,
            "retry-attempt-2",
            scheduled.Budget,
            null,
            scheduled.WakeCheckpointId,
            new string('e', 64),
            failure.EvidenceId,
            failure.ContentHash,
            scheduled.RecordedAtUtc);
        var substitutedDueFailure = GovernedLoopRetryContract.CreateState(
            series,
            3,
            GovernedLoopRetryStateDisposition.Due,
            1,
            "attempt-1",
            2,
            "retry-attempt-2",
            scheduled.Budget,
            null,
            scheduled.WakeCheckpointId,
            scheduled.WakeCheckpointHash,
            "substituted-failure",
            new string('e', 64),
            scheduled.RecordedAtUtc);
        var substitutedDispatchBudget = GovernedLoopRetryContract.CreateState(
            series,
            5,
            GovernedLoopRetryStateDisposition.Dispatched,
            1,
            "attempt-1",
            2,
            "retry-attempt-2",
            reservedBudget with { Tokens = 11 },
            null,
            scheduled.WakeCheckpointId,
            scheduled.WakeCheckpointHash,
            failure.EvidenceId,
            failure.ContentHash,
            scheduled.RecordedAtUtc);
        Assert.True(GovernedLoopRetryContract.IsValidTransition(scheduled, due));
        Assert.False(GovernedLoopRetryContract.IsValidTransition(scheduled, substitutedDueOperation));
        Assert.False(GovernedLoopRetryContract.IsValidTransition(scheduled, substitutedDueWake));
        Assert.False(GovernedLoopRetryContract.IsValidTransition(scheduled, substitutedDueFailure));
        Assert.True(GovernedLoopRetryContract.IsValidTransition(due, reserved));
        Assert.True(GovernedLoopRetryContract.IsValidTransition(reserved, dispatched));
        Assert.False(GovernedLoopRetryContract.IsValidTransition(reserved, substitutedDispatchBudget));
        Assert.True(GovernedLoopRetryContract.IsValidTransition(dispatched, completed));
        Assert.False(GovernedLoopRetryContract.IsValidTransition(dispatched, jumped));
        Assert.False(GovernedLoopRetryContract.IsValid(scheduled with { NextRetryAtUtc = null }));
        Assert.False(GovernedLoopRetryContract.IsValid(scheduled with { Budget = scheduled.Budget with { Attempts = 2 } }));
        Assert.False(GovernedLoopRetryContract.IsValid(scheduled with { NextRetryAtUtc = series.DeadlineUtc.AddTicks(1) }));
        Assert.False(GovernedLoopRetryContract.IsValid(scheduled with { FailureEvidenceHash = null! }));
    }

    private static GovernedLoopRetryPolicy Policy(
        GovernedLoopRetryBackoffStrategy backoff = GovernedLoopRetryBackoffStrategy.Exponential,
        long initialDelayMilliseconds = 100,
        long maximumDelayMilliseconds = 5_000,
        GovernedLoopRetryJitterStrategy jitter = GovernedLoopRetryJitterStrategy.DeterministicBounded,
        long maximumJitterMilliseconds = 25)
        => GovernedLoopRetryContract.CreatePolicy(
            "policy-1",
            "node-1",
            [GovernedLoopFailureClass.DependencyUnavailableBeforeDispatch, GovernedLoopFailureClass.RetryableNoEffect],
            ["dependency-unavailable", "retryable-no-effect"],
            4,
            10_000,
            60_000,
            backoff,
            initialDelayMilliseconds,
            maximumDelayMilliseconds,
            jitter,
            maximumJitterMilliseconds,
            maximumTokens: 10_000,
            maximumToolCalls: 8,
            maximumCostMicrounits: 1_000_000,
            maximumCostCurrency: "USD",
            maximumResourceUnits: 8);

    private static GovernedLoopFailureEvidence Failure()
        => GovernedLoopFailureEvidenceContract.Create(
            "failure-evidence",
            $"workspace-sha256:{new string('e', 64)}",
            "run-1",
            GovernedLoopRevisionReference.Create(1, "graph-1", "revision-1", new string('f', 64)),
            1,
            0,
            1,
            "node-1",
            1,
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
            DateTimeOffset.UnixEpoch.AddDays(1));
}
