using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Common.HumanReview;
using EmbodySense.Core.Common.HumanReview.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Startup.Loops.Execution.Sleep;
using EmbodySense.Core.Startup.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution.Sleep;

public sealed class HumanReviewRecoveryRunnerTests
{
    private static readonly DateTimeOffset _now = new(2026, 8, 31, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Non_human_review_work_is_delegated_and_human_review_pass_is_serialized()
    {
        var inner = new HumanReviewRecoveryRecordingWorkRunner();
        var runs = new HumanReviewRecoveryRecordingRunStore
        {
            PageFactory = request => new CustomLoopRunPage([], request.Cursor is null ? "publication-page-1" : null),
        };
        var continuations = new HumanReviewRecoveryRecordingContinuationStore
        {
            PageFactory = request => new HumanReviewContinuationRecoveryPage(HumanReviewContinuationRecoveryPageStatus.Current, [], request.ScanCursor is null ? "continuation-page-1" : null, false),
        };
        var actions = new HumanReviewRecoveryRecordingDecisionActionStore
        {
            PageFactory = request => new HumanReviewDecisionActionRecoveryPage(HumanReviewDecisionActionRecoveryPageStatus.Current, [], request.ScanCursor is null ? "action-page-1" : null, false),
        };
        var runner = CreateRunner(inner, runs, continuations, actions);

        var delegated = await runner.RunOnceAsync(GovernedLoopLocalWorkFamily.Schedule);
        var first = await Task.WhenAll(
            runner.RunOnceAsync(GovernedLoopLocalWorkFamily.HumanReview),
            runner.RunOnceAsync(GovernedLoopLocalWorkFamily.HumanReview));

        Assert.Equal("delegated", delegated?.ReasonCode);
        Assert.All(first, result => Assert.Equal(GovernedLoopLocalWorkResultStatus.Empty, result?.Status));
        Assert.Equal(1, inner.Calls);
        Assert.Equal([null, "publication-page-1"], runs.Cursors);
        Assert.Equal([null, "continuation-page-1"], continuations.Cursors);
        Assert.Equal([null, "action-page-1"], actions.Cursors);
        Assert.Equal(1, runs.MaxConcurrent);
        Assert.True(runner.IsExecutable);
    }

    [Fact]
    public async Task Recovery_lanes_advance_independent_opaque_cursors()
    {
        var runs = new HumanReviewRecoveryRecordingRunStore
        {
            PageFactory = request => new CustomLoopRunPage([], request.Cursor is null ? "publication-next" : null),
        };
        var continuations = new HumanReviewRecoveryRecordingContinuationStore
        {
            PageFactory = request => new HumanReviewContinuationRecoveryPage(HumanReviewContinuationRecoveryPageStatus.Current, [], request.ScanCursor is null ? "continuation-next" : null, false),
        };
        var actions = new HumanReviewRecoveryRecordingDecisionActionStore
        {
            PageFactory = request => new HumanReviewDecisionActionRecoveryPage(HumanReviewDecisionActionRecoveryPageStatus.Current, [], request.ScanCursor is null ? "action-next" : null, false),
        };
        var runner = CreateRunner(new HumanReviewRecoveryRecordingWorkRunner(), runs, continuations, actions);

        var first = await runner.RecoverAsync();
        var second = await runner.RecoverAsync();

        Assert.Equal(HumanReviewRecoveryPassStatus.Current, first.Status);
        Assert.Equal(HumanReviewRecoveryPassStatus.Current, second.Status);
        Assert.Equal([null, "publication-next"], runs.Cursors);
        Assert.Equal([null, "continuation-next"], continuations.Cursors);
        Assert.Equal([null, "action-next"], actions.Cursors);
        Assert.Equal("publication-next", first.Publication.NextScanCursor);
        Assert.Equal("continuation-next", first.ContinuationScanCursor);
        Assert.Equal("action-next", first.DecisionActionScanCursor);
    }

    [Fact]
    public async Task Invalid_canonical_page_fails_closed_without_delegating_or_dispatching()
    {
        var inner = new HumanReviewRecoveryRecordingWorkRunner();
        var runs = new HumanReviewRecoveryRecordingRunStore
        {
            PageFactory = _ => new CustomLoopRunPage([null!], null),
        };
        var continuations = new HumanReviewRecoveryRecordingContinuationStore();
        var actions = new HumanReviewRecoveryRecordingDecisionActionStore();
        var runner = CreateRunner(inner, runs, continuations, actions);

        var result = await runner.RunOnceAsync(GovernedLoopLocalWorkFamily.HumanReview);

        Assert.Equal(GovernedLoopLocalWorkResultStatus.Corrupt, result?.Status);
        Assert.Equal("human-review-recovery-corrupt", result?.ReasonCode);
        Assert.Equal([null], continuations.Cursors);
        Assert.Equal([null], actions.Cursors);
        Assert.False(runner.IsExecutable);
    }

    [Fact]
    public async Task Unavailable_canonical_page_is_reported_without_advancing_cursor()
    {
        var runs = new HumanReviewRecoveryRecordingRunStore
        {
            PageFactory = _ => throw new IOException("unavailable"),
        };
        var runner = CreateRunner(new HumanReviewRecoveryRecordingWorkRunner(), runs, new HumanReviewRecoveryRecordingContinuationStore(), new HumanReviewRecoveryRecordingDecisionActionStore());

        var result = await runner.RunOnceAsync(GovernedLoopLocalWorkFamily.HumanReview);

        Assert.Equal(GovernedLoopLocalWorkResultStatus.Unavailable, result?.Status);
        Assert.Equal("human-review-recovery-unavailable", result?.ReasonCode);
        Assert.False(runner.IsExecutable);
    }

    [Fact]
    public async Task Invalid_continuation_page_retains_its_opaque_cursor()
    {
        var continuationPageCalls = 0;
        var continuations = new HumanReviewRecoveryRecordingContinuationStore
        {
            PageFactory = request => continuationPageCalls++ == 0
                ? new HumanReviewContinuationRecoveryPage(HumanReviewContinuationRecoveryPageStatus.Current, [], "continuation-next", true)
                : new HumanReviewContinuationRecoveryPage(HumanReviewContinuationRecoveryPageStatus.Invalid, [], null, false)
        };
        var runner = CreateRunner(
            new HumanReviewRecoveryRecordingWorkRunner(),
            EmptyRuns(),
            continuations,
            new HumanReviewRecoveryRecordingDecisionActionStore());

        var first = await runner.RecoverAsync();
        var second = await runner.RecoverAsync();

        Assert.Equal(HumanReviewRecoveryPassStatus.Current, first.Status);
        Assert.Equal(HumanReviewRecoveryPassStatus.Invalid, second.Status);
        Assert.Equal([null, "continuation-next"], continuations.Cursors);
    }

    [Fact]
    public async Task Unavailable_decision_action_page_retains_its_opaque_cursor()
    {
        var actionPageCalls = 0;
        var actions = new HumanReviewRecoveryRecordingDecisionActionStore
        {
            PageFactory = request => actionPageCalls++ == 0
                ? new HumanReviewDecisionActionRecoveryPage(HumanReviewDecisionActionRecoveryPageStatus.Current, [], "action-next", true)
                : throw new IOException("unavailable")
        };
        var runner = CreateRunner(new HumanReviewRecoveryRecordingWorkRunner(), EmptyRuns(), new HumanReviewRecoveryRecordingContinuationStore(), actions);

        var first = await runner.RecoverAsync();
        var second = await runner.RecoverAsync();

        Assert.Equal(HumanReviewRecoveryPassStatus.Current, first.Status);
        Assert.Equal(HumanReviewRecoveryPassStatus.Unavailable, second.Status);
        Assert.Equal([null, "action-next"], actions.Cursors);
    }

    [Fact]
    public async Task Deleted_tombstone_is_skipped_but_missing_live_run_is_corrupt()
    {
        var tombstone = Summary(isDeleted: true);
        var tombstoneRuns = EmptyRuns();
        tombstoneRuns.PageFactory = _ => new CustomLoopRunPage([tombstone], null);
        tombstoneRuns.GetFactory = _ => throw new InvalidOperationException("tombstones must not be reread");
        var tombstoneRunner = CreateRunner(new HumanReviewRecoveryRecordingWorkRunner(), tombstoneRuns, new HumanReviewRecoveryRecordingContinuationStore(), new HumanReviewRecoveryRecordingDecisionActionStore());

        var tombstoneResult = await tombstoneRunner.RecoverAsync();

        Assert.Equal(HumanReviewRecoveryPassStatus.Current, tombstoneResult.Status);
        Assert.Equal(0, tombstoneRuns.GetCalls);

        var liveSummary = Summary(isDeleted: false);
        var missingRunStore = EmptyRuns();
        missingRunStore.PageFactory = _ => new CustomLoopRunPage([liveSummary], "must-not-advance");
        missingRunStore.GetFactory = _ => null;
        var missingRunRunner = CreateRunner(new HumanReviewRecoveryRecordingWorkRunner(), missingRunStore, new HumanReviewRecoveryRecordingContinuationStore(), new HumanReviewRecoveryRecordingDecisionActionStore());

        var missingRunResult = await missingRunRunner.RecoverAsync();
        var missingRunRetry = await missingRunRunner.RecoverAsync();

        Assert.Equal(HumanReviewRecoveryPassStatus.Invalid, missingRunResult.Status);
        Assert.Equal(HumanReviewRecoveryPassStatus.Invalid, missingRunRetry.Status);
        Assert.Equal(2, missingRunStore.GetCalls);
        Assert.Equal([null, null], missingRunStore.Cursors);
    }

    [Fact]
    public async Task Duplicate_summaries_fail_closed_before_any_recovery_lane_runs()
    {
        var duplicate = Summary(isDeleted: true);
        var runs = EmptyRuns();
        runs.PageFactory = _ => new CustomLoopRunPage([duplicate, duplicate], null);
        var continuations = new HumanReviewRecoveryRecordingContinuationStore();
        var actions = new HumanReviewRecoveryRecordingDecisionActionStore();
        var runner = CreateRunner(new HumanReviewRecoveryRecordingWorkRunner(), runs, continuations, actions);

        var result = await runner.RecoverAsync();

        Assert.Equal(HumanReviewRecoveryPassStatus.Invalid, result.Status);
        Assert.Equal([null], continuations.Cursors);
        Assert.Equal([null], actions.Cursors);
    }

    [Fact]
    public async Task Malformed_summary_fails_closed_without_advancing_publication_cursor()
    {
        var malformed = Summary(isDeleted: true) with { DefinitionVersion = 0 };
        var runs = EmptyRuns();
        runs.PageFactory = request => new CustomLoopRunPage([malformed], request.Cursor is null ? "should-not-advance" : null);
        var runner = CreateRunner(new HumanReviewRecoveryRecordingWorkRunner(), runs, new HumanReviewRecoveryRecordingContinuationStore(), new HumanReviewRecoveryRecordingDecisionActionStore());

        var result = await runner.RecoverAsync();

        Assert.Equal(HumanReviewRecoveryPassStatus.Invalid, result.Status);
        Assert.Equal([null], runs.Cursors);
    }

    [Fact]
    public async Task Oversized_publication_cursor_fails_closed_and_retries_from_retained_cursor()
    {
        var oversizedCursor = new string('x', CustomLoopLimits.MaxRunPageCursorCharacters + 1);
        var oversizedPageCalls = 0;
        var runs = EmptyRuns();
        runs.PageFactory = request => request.Cursor switch
        {
            null => new CustomLoopRunPage([Summary(isDeleted: true)], "publication-valid"),
            "publication-valid" when oversizedPageCalls++ == 0 => new CustomLoopRunPage([], oversizedCursor),
            _ => new CustomLoopRunPage([], null),
        };
        var runner = CreateRunner(new HumanReviewRecoveryRecordingWorkRunner(), runs, new HumanReviewRecoveryRecordingContinuationStore(), new HumanReviewRecoveryRecordingDecisionActionStore());

        var first = await runner.RecoverAsync();
        var second = await runner.RecoverAsync();
        var third = await runner.RecoverAsync();

        Assert.Equal(HumanReviewRecoveryPassStatus.Current, first.Status);
        Assert.Equal(HumanReviewRecoveryPassStatus.Invalid, second.Status);
        Assert.Equal(HumanReviewRecoveryPassStatus.Current, third.Status);
        Assert.Equal([null, "publication-valid", "publication-valid"], runs.Cursors);
        Assert.Equal("publication-valid", second.Publication.NextScanCursor);
    }

    [Fact]
    public async Task Oversized_continuation_cursor_fails_closed_and_retries_from_retained_cursor()
    {
        var oversizedCursor = new string('x', CustomLoopLimits.MaxRunPageCursorCharacters + 1);
        var oversizedPageCalls = 0;
        var continuations = new HumanReviewRecoveryRecordingContinuationStore
        {
            PageFactory = request => request.ScanCursor switch
            {
                null => new HumanReviewContinuationRecoveryPage(HumanReviewContinuationRecoveryPageStatus.Current, [], "continuation-valid", true),
                "continuation-valid" when oversizedPageCalls++ == 0 => new HumanReviewContinuationRecoveryPage(HumanReviewContinuationRecoveryPageStatus.Current, [], oversizedCursor, true),
                _ => new HumanReviewContinuationRecoveryPage(HumanReviewContinuationRecoveryPageStatus.Current, [], null, false),
            },
        };
        var runner = CreateRunner(new HumanReviewRecoveryRecordingWorkRunner(), EmptyRuns(), continuations, new HumanReviewRecoveryRecordingDecisionActionStore());

        var first = await runner.RecoverAsync();
        var second = await runner.RecoverAsync();
        var third = await runner.RecoverAsync();

        Assert.Equal(HumanReviewRecoveryPassStatus.Current, first.Status);
        Assert.Equal(HumanReviewRecoveryPassStatus.Invalid, second.Status);
        Assert.Equal(HumanReviewRecoveryPassStatus.Current, third.Status);
        Assert.Equal([null, "continuation-valid", "continuation-valid"], continuations.Cursors);
        Assert.Equal("continuation-valid", second.ContinuationScanCursor);
    }

    [Fact]
    public async Task Oversized_decision_action_cursor_fails_closed_and_retries_from_retained_cursor()
    {
        var oversizedCursor = new string('x', CustomLoopLimits.MaxRunPageCursorCharacters + 1);
        var oversizedPageCalls = 0;
        var actions = new HumanReviewRecoveryRecordingDecisionActionStore
        {
            PageFactory = request => request.ScanCursor switch
            {
                null => new HumanReviewDecisionActionRecoveryPage(HumanReviewDecisionActionRecoveryPageStatus.Current, [], "action-valid", true),
                "action-valid" when oversizedPageCalls++ == 0 => new HumanReviewDecisionActionRecoveryPage(HumanReviewDecisionActionRecoveryPageStatus.Current, [], oversizedCursor, true),
                _ => new HumanReviewDecisionActionRecoveryPage(HumanReviewDecisionActionRecoveryPageStatus.Current, [], null, false),
            },
        };
        var runner = CreateRunner(new HumanReviewRecoveryRecordingWorkRunner(), EmptyRuns(), new HumanReviewRecoveryRecordingContinuationStore(), actions);

        var first = await runner.RecoverAsync();
        var second = await runner.RecoverAsync();
        var third = await runner.RecoverAsync();

        Assert.Equal(HumanReviewRecoveryPassStatus.Current, first.Status);
        Assert.Equal(HumanReviewRecoveryPassStatus.Invalid, second.Status);
        Assert.Equal(HumanReviewRecoveryPassStatus.Current, third.Status);
        Assert.Equal([null, "action-valid", "action-valid"], actions.Cursors);
        Assert.Equal("action-valid", second.DecisionActionScanCursor);
    }

    [Fact]
    public async Task Parked_publication_defers_to_a_later_page_and_wraps_without_duplicate_publication()
    {
        var firstRun = await HumanReviewRecoveryCanonicalRunFactory.CreateApprovedRunAsync("run-page-one", "admission-page-one");
        var secondRun = await HumanReviewRecoveryCanonicalRunFactory.CreateApprovedRunAsync("run-page-two", "admission-page-two");
        var firstPage = Summary(firstRun);
        var secondPage = Summary(secondRun);
        var runs = EmptyRuns();
        runs.PageFactory = request => request.Cursor switch
        {
            null => new CustomLoopRunPage([firstPage], "publication-page-two"),
            "publication-page-two" => new CustomLoopRunPage([secondPage], null),
            _ => throw new InvalidOperationException("Unexpected publication cursor."),
        };
        runs.GetFactory = runId => runId switch
        {
            "run-page-one" => firstRun,
            "run-page-two" => secondRun,
            _ => null,
        };
        var publication = new HumanReviewRecoveryRecordingPublicationService
        {
            StatusFactory = runId => string.Equals(runId, "run-page-one", StringComparison.Ordinal)
                ? HumanReviewContinuationStoreMutationStatus.Conflict
                : HumanReviewContinuationStoreMutationStatus.Committed,
        };
        var runner = CreateRunner(new HumanReviewRecoveryRecordingWorkRunner(), runs, new HumanReviewRecoveryRecordingContinuationStore(), new HumanReviewRecoveryRecordingDecisionActionStore(), publication);

        var first = await runner.RecoverAsync();
        var second = await runner.RecoverAsync();
        var third = await runner.RecoverAsync();

        Assert.Equal(HumanReviewRecoveryPassStatus.Current, first.Status);
        Assert.Equal(HumanReviewRecoveryPassStatus.Current, second.Status);
        Assert.Equal(HumanReviewRecoveryPassStatus.Current, third.Status);
        Assert.Equal(["run-page-one", "run-page-two", "run-page-one"], publication.Calls);
        Assert.Equal([null, "publication-page-two", null], runs.Cursors);
        Assert.Equal(HumanReviewPublicationRecoveryItemStatus.Parked, Assert.Single(first.Publication.Items).Status);
        Assert.Equal(HumanReviewPublicationRecoveryItemStatus.Published, Assert.Single(second.Publication.Items).Status);
        Assert.Equal(HumanReviewPublicationRecoveryItemStatus.Parked, Assert.Single(third.Publication.Items).Status);
        Assert.True(CustomLoopRunValidator.Validate(firstRun).IsValid);
        Assert.True(CustomLoopRunValidator.Validate(secondRun).IsValid);
        Assert.Equal(3, runs.GetCalls);
    }

    [Theory]
    [InlineData(HumanReviewContinuationRecoveryItemStatus.ClaimConflict, GovernedLoopLocalWorkResultStatus.Conflict)]
    [InlineData(HumanReviewContinuationRecoveryItemStatus.StaleAfterClaim, GovernedLoopLocalWorkResultStatus.Conflict)]
    [InlineData(HumanReviewContinuationRecoveryItemStatus.Parked, GovernedLoopLocalWorkResultStatus.AttentionRequired)]
    [InlineData(HumanReviewContinuationRecoveryItemStatus.ExpiredWakeRetained, GovernedLoopLocalWorkResultStatus.AttentionRequired)]
    public async Task Continuation_recovery_item_statuses_map_to_safe_local_posture(HumanReviewContinuationRecoveryItemStatus itemStatus, GovernedLoopLocalWorkResultStatus expected)
    {
        var candidate = ContinuationCandidate(itemStatus == HumanReviewContinuationRecoveryItemStatus.ExpiredWakeRetained);
        var continuations = new HumanReviewRecoveryRecordingContinuationStore
        {
            PageFactory = _ => new HumanReviewContinuationRecoveryPage(HumanReviewContinuationRecoveryPageStatus.Current, [candidate], null, false),
            ClaimResult = itemStatus == HumanReviewContinuationRecoveryItemStatus.StaleAfterClaim
                ? new HumanReviewContinuationStoreMutationResult(HumanReviewContinuationStoreMutationStatus.Committed)
                : itemStatus == HumanReviewContinuationRecoveryItemStatus.ClaimConflict
                    ? new HumanReviewContinuationStoreMutationResult(HumanReviewContinuationStoreMutationStatus.Conflict)
                    : new HumanReviewContinuationStoreMutationResult(HumanReviewContinuationStoreMutationStatus.Unavailable),
            ReadResult = new HumanReviewContinuationCandidateReadResult(HumanReviewContinuationCandidateReadStatus.Stale)
        };
        var runner = CreateRunner(new HumanReviewRecoveryRecordingWorkRunner(), EmptyRuns(), continuations, new HumanReviewRecoveryRecordingDecisionActionStore());

        var result = await runner.RunOnceAsync(GovernedLoopLocalWorkFamily.HumanReview);

        Assert.Equal(expected, result?.Status);
    }

    [Theory]
    [InlineData(HumanReviewDecisionActionRecoveryItemStatus.ClaimConflict, GovernedLoopLocalWorkResultStatus.Conflict)]
    [InlineData(HumanReviewDecisionActionRecoveryItemStatus.StaleAfterClaim, GovernedLoopLocalWorkResultStatus.Conflict)]
    [InlineData(HumanReviewDecisionActionRecoveryItemStatus.Parked, GovernedLoopLocalWorkResultStatus.AttentionRequired)]
    public async Task Decision_action_recovery_item_statuses_map_to_safe_local_posture(HumanReviewDecisionActionRecoveryItemStatus itemStatus, GovernedLoopLocalWorkResultStatus expected)
    {
        var candidate = DecisionActionCandidate();
        var actions = new HumanReviewRecoveryRecordingDecisionActionStore
        {
            PageFactory = _ => new HumanReviewDecisionActionRecoveryPage(HumanReviewDecisionActionRecoveryPageStatus.Current, [candidate], null, false),
            ClaimResult = itemStatus == HumanReviewDecisionActionRecoveryItemStatus.StaleAfterClaim
                ? new HumanReviewDecisionActionStoreMutationResult(HumanReviewDecisionActionStoreMutationStatus.Committed)
                : itemStatus == HumanReviewDecisionActionRecoveryItemStatus.ClaimConflict
                    ? new HumanReviewDecisionActionStoreMutationResult(HumanReviewDecisionActionStoreMutationStatus.Conflict)
                    : new HumanReviewDecisionActionStoreMutationResult(HumanReviewDecisionActionStoreMutationStatus.Unavailable),
            ReadResult = new HumanReviewDecisionActionCandidateReadResult(HumanReviewDecisionActionCandidateReadStatus.Stale)
        };
        var runner = CreateRunner(new HumanReviewRecoveryRecordingWorkRunner(), EmptyRuns(), new HumanReviewRecoveryRecordingContinuationStore(), actions);

        var result = await runner.RunOnceAsync(GovernedLoopLocalWorkFamily.HumanReview);

        Assert.Equal(expected, result?.Status);
    }

    [Fact]
    public async Task Consecutive_healthy_passes_share_one_rate_bounded_aggregate_readiness_probe()
    {
        var probeCalls = 0;
        var readiness = new HumanReviewRecoveryReadinessSignal(
            _ =>
            {
                probeCalls++;
                return Task.FromResult(true);
            },
            aggregateProbeInterval: TimeSpan.FromHours(1));
        var runner = CreateRunner(new HumanReviewRecoveryRecordingWorkRunner(), EmptyRuns(), new HumanReviewRecoveryRecordingContinuationStore(), new HumanReviewRecoveryRecordingDecisionActionStore(), readiness: readiness);

        var first = await runner.RecoverAsync();
        var second = await runner.RecoverAsync();

        Assert.Equal(HumanReviewRecoveryPassStatus.Current, first.Status);
        Assert.Equal(HumanReviewRecoveryPassStatus.Current, second.Status);
        Assert.Equal(1, probeCalls);
        Assert.True(runner.IsExecutable);
    }

    [Fact]
    public async Task Caller_cancellation_reaches_the_in_flight_aggregate_readiness_probe()
    {
        var probeStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var readiness = new HumanReviewRecoveryReadinessSignal(
            async cancellationToken =>
            {
                probeStarted.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return true;
            },
            aggregateProbeInterval: TimeSpan.FromHours(1));
        var runner = CreateRunner(new HumanReviewRecoveryRecordingWorkRunner(), EmptyRuns(), new HumanReviewRecoveryRecordingContinuationStore(), new HumanReviewRecoveryRecordingDecisionActionStore(), readiness: readiness);
        using var cancellation = new CancellationTokenSource();

        var recovery = runner.RecoverAsync(cancellation.Token);
        await probeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recovery);
        Assert.False(runner.IsExecutable);
    }

    private static HumanReviewRecoveryRunner CreateRunner(
        HumanReviewRecoveryRecordingWorkRunner inner,
        HumanReviewRecoveryRecordingRunStore runs,
        HumanReviewRecoveryRecordingContinuationStore continuations,
        HumanReviewRecoveryRecordingDecisionActionStore actions,
        HumanReviewRecoveryRecordingPublicationService? publication = null,
        HumanReviewRecoveryReadinessSignal? readiness = null)
        => HumanReviewRecoveryRunnerTestFactory.Create(inner, runs, continuations, actions, _now, publication, readiness);

    private static HumanReviewRecoveryRecordingRunStore EmptyRuns() => new();

    private static CustomLoopRunSummary Summary(bool isDeleted)
        => new("run-deleted", "loop-one", "admission-one", 1, isDeleted ? 0 : 1, isDeleted ? CustomLoopRunStatus.Completed : CustomLoopRunStatus.Paused, _now, _now, isDeleted ? _now : null, 0, 0, null, isDeleted);

    private static CustomLoopRunSummary Summary(CustomLoopRunRecord run)
        => new(run.Id, run.LoopId, run.AdmissionOperationId, run.AdmittedDefinition.DefinitionVersion, run.LifecycleVersion, run.Status, run.CreatedAtUtc, run.UpdatedAtUtc, run.CompletedAtUtc, run.Checkpoint.Iteration, run.Checkpoint.NextStepIndex, run.FailureCode, false);

    private static HumanReviewContinuationRecoveryCandidate ContinuationCandidate(bool expired)
        => new("run-one", 7, new("request-one", Hash), new("decision-one", "operation-one", HumanReviewDecisionKind.Approve, Hash), new("wake-one", Hash), 1, expired ? _now : _now.AddMinutes(30), new("reservation-one", Hash), null);

    private static HumanReviewDecisionActionRecoveryCandidate DecisionActionCandidate()
        => new("run-two", 7, new("request-two", Hash), new("decision-two", "operation-two", HumanReviewDecisionKind.Reject, Hash), new("wake-two", Hash), 1, _now.AddMinutes(30), new("reservation-two", Hash), null);

    private static string Hash => new('a', HumanReviewContractLimits.Sha256HexCharacters);

}
