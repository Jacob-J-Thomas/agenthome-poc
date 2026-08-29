using EmbodySense.Core.Application.HumanInput.Continuations;
using EmbodySense.Core.Application.HumanInput.Continuations.Models;
using EmbodySense.Core.Application.HumanInput.Policies.Models;
using EmbodySense.Core.Common.Loops.HumanInput.Policies;
using EmbodySense.Core.Common.Loops.HumanInput.Policies.Models;
using EmbodySense.Core.Startup.Loops.Execution.Sleep;
using EmbodySense.Core.Startup.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution.Sleep;

public sealed class HumanInputResponseContinuationWorkRunnerTests
{
    private static readonly DateTimeOffset _now = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Completed_is_reported_only_for_closed_durable_continuation_outcomes()
    {
        var first = Candidate("one");
        var second = Candidate("two");
        var third = Candidate("three");
        var source = new HumanInputResponseContinuationRecordingCandidateSource(Page([first, second, third], "cursor-one"));
        var continuation = new HumanInputResponseContinuationRecordingWakePort(
            HumanInputResponseContinuationWakeStatus.Submitted,
            HumanInputResponseContinuationWakeStatus.Replayed,
            HumanInputResponseContinuationWakeStatus.Retired);
        var runner = Runner(source, continuation);

        var submitted = await runner.RunOnceAsync(GovernedLoopLocalWorkFamily.HumanInput);
        var replayed = await runner.RunOnceAsync(GovernedLoopLocalWorkFamily.HumanInput);
        var retired = await runner.RunOnceAsync(GovernedLoopLocalWorkFamily.HumanInput);

        Assert.All([submitted, replayed, retired], result => Assert.Equal(GovernedLoopLocalWorkResultStatus.Completed, result?.Status));
        Assert.Equal([first, second, third], continuation.Candidates);
        Assert.Equal([null], source.Cursors);
    }

    [Fact]
    public async Task No_work_discards_the_current_candidate_and_advances_the_transient_scan_page()
    {
        var first = Candidate("one");
        var second = Candidate("two");
        var source = new HumanInputResponseContinuationRecordingCandidateSource(Page([first, second], "cursor-one"));
        var continuation = new HumanInputResponseContinuationRecordingWakePort(
            HumanInputResponseContinuationWakeStatus.NoWork,
            HumanInputResponseContinuationWakeStatus.Submitted);
        var runner = Runner(source, continuation);

        var noWork = await runner.RunOnceAsync(GovernedLoopLocalWorkFamily.HumanInput);
        var submitted = await runner.RunOnceAsync(GovernedLoopLocalWorkFamily.HumanInput);

        Assert.Equal(GovernedLoopLocalWorkResultStatus.Empty, noWork?.Status);
        Assert.Equal(GovernedLoopLocalWorkResultStatus.Completed, submitted?.Status);
        Assert.Equal([first, second], continuation.Candidates);
        Assert.Equal([null], source.Cursors);
    }

    [Fact]
    public async Task Stale_discards_the_current_candidate_without_reclassifying_it_as_completed()
    {
        var first = Candidate("one");
        var second = Candidate("two");
        var source = new HumanInputResponseContinuationRecordingCandidateSource(Page([first, second], "cursor-one"));
        var continuation = new HumanInputResponseContinuationRecordingWakePort(
            HumanInputResponseContinuationWakeStatus.Stale,
            HumanInputResponseContinuationWakeStatus.Submitted);
        var runner = Runner(source, continuation);

        var stale = await runner.RunOnceAsync(GovernedLoopLocalWorkFamily.HumanInput);
        var submitted = await runner.RunOnceAsync(GovernedLoopLocalWorkFamily.HumanInput);

        Assert.Equal(GovernedLoopLocalWorkResultStatus.Empty, stale?.Status);
        Assert.Equal(GovernedLoopLocalWorkResultStatus.Completed, submitted?.Status);
        Assert.Equal([first, second], continuation.Candidates);
        Assert.Equal([null], source.Cursors);
    }

    [Fact]
    public async Task Empty_pages_advance_only_through_the_opaque_cursor_and_restart_after_a_clean_tail_probe()
    {
        var candidate = Candidate("one");
        var source = new HumanInputResponseContinuationRecordingCandidateSource(
            Page([], "cursor-one"),
            Page([candidate], "cursor-two"),
            Page([], null),
            Page([], null));
        var runner = Runner(source, new HumanInputResponseContinuationRecordingWakePort(HumanInputResponseContinuationWakeStatus.NoWork));

        var first = await runner.RunOnceAsync(GovernedLoopLocalWorkFamily.HumanInput);
        var second = await runner.RunOnceAsync(GovernedLoopLocalWorkFamily.HumanInput);
        var tail = await runner.RunOnceAsync(GovernedLoopLocalWorkFamily.HumanInput);
        var fresh = await runner.RunOnceAsync(GovernedLoopLocalWorkFamily.HumanInput);

        Assert.All([first, second, tail, fresh], result => Assert.Equal(GovernedLoopLocalWorkResultStatus.Empty, result?.Status));
        Assert.Equal([null, "cursor-one", "cursor-two", null], source.Cursors);
        Assert.Equal([candidate], source.Candidates);
    }

    [Fact]
    public async Task Unavailable_preserves_the_detached_candidate_and_a_restarted_runner_begins_a_fresh_clean_scan()
    {
        var candidate = Candidate("one");
        var source = new HumanInputResponseContinuationRecordingCandidateSource(Page([candidate], "cursor-one"));
        var unavailable = new HumanInputResponseContinuationRecordingWakePort(
            HumanInputResponseContinuationWakeStatus.Unavailable,
            HumanInputResponseContinuationWakeStatus.Unavailable);
        var runner = Runner(source, unavailable);

        var first = await runner.RunOnceAsync(GovernedLoopLocalWorkFamily.HumanInput);
        var retry = await runner.RunOnceAsync(GovernedLoopLocalWorkFamily.HumanInput);
        var restarted = Runner(source, new HumanInputResponseContinuationRecordingWakePort(HumanInputResponseContinuationWakeStatus.NoWork));
        var afterRestart = await restarted.RunOnceAsync(GovernedLoopLocalWorkFamily.HumanInput);

        Assert.Equal(GovernedLoopLocalWorkResultStatus.Unavailable, first?.Status);
        Assert.Equal(GovernedLoopLocalWorkResultStatus.Unavailable, retry?.Status);
        Assert.Equal(GovernedLoopLocalWorkResultStatus.Empty, afterRestart?.Status);
        Assert.Equal([candidate, candidate], unavailable.Candidates);
        Assert.Equal([null, null], source.Cursors);
    }

    [Fact]
    public async Task Invalid_recovery_evidence_is_a_fatal_fail_closed_result_without_dispatch()
    {
        var source = new HumanInputResponseContinuationRecordingCandidateSource(new HumanInputResponseContinuationRecoveryPage(
            HumanInputResponseContinuationRecoveryPageStatus.Invalid,
            [],
            null,
            false));
        var continuation = new HumanInputResponseContinuationRecordingWakePort(HumanInputResponseContinuationWakeStatus.Submitted);
        var runner = Runner(source, continuation);

        var result = await runner.RunOnceAsync(GovernedLoopLocalWorkFamily.HumanInput);

        Assert.Equal(GovernedLoopLocalWorkResultStatus.Corrupt, result?.Status);
        Assert.Empty(continuation.Candidates);
    }

    [Fact]
    public async Task Current_page_with_candidates_and_no_cursor_is_rejected_without_dispatch()
    {
        var source = new HumanInputResponseContinuationRecordingCandidateSource(new HumanInputResponseContinuationRecoveryPage(
            HumanInputResponseContinuationRecoveryPageStatus.Current,
            [Candidate("one")],
            null,
            false));
        var continuation = new HumanInputResponseContinuationRecordingWakePort(HumanInputResponseContinuationWakeStatus.Submitted);
        var runner = Runner(source, continuation);

        var result = await runner.RunOnceAsync(GovernedLoopLocalWorkFamily.HumanInput);

        Assert.Equal(GovernedLoopLocalWorkResultStatus.Corrupt, result?.Status);
        Assert.False(runner.IsExecutable);
        Assert.Empty(continuation.Candidates);
    }

    [Fact]
    public async Task Unavailable_recovery_evidence_and_source_fault_preserve_fail_closed_unavailable_posture()
    {
        var unavailableSource = new HumanInputResponseContinuationRecordingCandidateSource(new HumanInputResponseContinuationRecoveryPage(
            HumanInputResponseContinuationRecoveryPageStatus.Unavailable,
            [],
            null,
            false));
        var unavailable = await Runner(unavailableSource, new HumanInputResponseContinuationRecordingWakePort(HumanInputResponseContinuationWakeStatus.Submitted))
            .RunOnceAsync(GovernedLoopLocalWorkFamily.HumanInput);
        var throwingSource = new HumanInputResponseContinuationThrowingCandidateSource();
        var faulted = await new HumanInputResponseContinuationWorkRunner(
                new HumanInputResponseContinuationRecordingLocalWorkRunner(),
                throwingSource,
                HealthyPolicySource(),
                new HumanInputResponseContinuationRecordingWakePort(HumanInputResponseContinuationWakeStatus.Submitted),
                4,
                new HumanInputResponseContinuationFixedTimeProvider(_now))
            .RunOnceAsync(GovernedLoopLocalWorkFamily.HumanInput);

        Assert.Equal(GovernedLoopLocalWorkResultStatus.Unavailable, unavailable?.Status);
        Assert.Equal(GovernedLoopLocalWorkResultStatus.Unavailable, faulted?.Status);
        Assert.Equal([null], unavailableSource.Cursors);
        Assert.Equal(1, throwingSource.Calls);
    }

    [Fact]
    public async Task Invalid_continuation_result_is_a_fatal_fail_closed_result_without_advancing_the_candidate()
    {
        var candidate = Candidate("one");
        var source = new HumanInputResponseContinuationRecordingCandidateSource(Page([candidate], "cursor-one"));
        var continuation = new HumanInputResponseContinuationRecordingWakePort(
            HumanInputResponseContinuationWakeStatus.Invalid,
            HumanInputResponseContinuationWakeStatus.Invalid);
        var runner = Runner(source, continuation);

        var first = await runner.RunOnceAsync(GovernedLoopLocalWorkFamily.HumanInput);
        var retry = await runner.RunOnceAsync(GovernedLoopLocalWorkFamily.HumanInput);

        Assert.Equal(GovernedLoopLocalWorkResultStatus.Corrupt, first?.Status);
        Assert.Equal(GovernedLoopLocalWorkResultStatus.Corrupt, retry?.Status);
        Assert.Equal([candidate, candidate], continuation.Candidates);
        Assert.Equal([null], source.Cursors);
    }

    [Fact]
    public async Task Other_families_remain_owned_by_the_existing_runner()
    {
        var inner = new HumanInputResponseContinuationRecordingLocalWorkRunner();
        var runner = new HumanInputResponseContinuationWorkRunner(
            inner,
            new HumanInputResponseContinuationRecordingCandidateSource(Page([], null)),
            HealthyPolicySource(),
            new HumanInputResponseContinuationRecordingWakePort(HumanInputResponseContinuationWakeStatus.NoWork),
            4,
            new HumanInputResponseContinuationFixedTimeProvider(_now));

        var result = await runner.RunOnceAsync(GovernedLoopLocalWorkFamily.Wake);

        Assert.Same(inner.Result, result);
        Assert.Equal([GovernedLoopLocalWorkFamily.Wake], inner.Families);
    }

    [Fact]
    public async Task Cancellation_and_untrusted_clock_fail_before_source_or_continuation_dispatch()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var source = new HumanInputResponseContinuationRecordingCandidateSource(Page([Candidate("one")], "cursor-one"));
        var cancelled = Runner(source, new HumanInputResponseContinuationRecordingWakePort(HumanInputResponseContinuationWakeStatus.Submitted));
        var unavailableClock = new HumanInputResponseContinuationWorkRunner(
            new HumanInputResponseContinuationRecordingLocalWorkRunner(),
            source,
            HealthyPolicySource(),
            new HumanInputResponseContinuationRecordingWakePort(HumanInputResponseContinuationWakeStatus.Submitted),
            4,
            new HumanInputResponseContinuationThrowingTimeProvider());
        var corruptClock = new HumanInputResponseContinuationWorkRunner(
            new HumanInputResponseContinuationRecordingLocalWorkRunner(),
            source,
            HealthyPolicySource(),
            new HumanInputResponseContinuationRecordingWakePort(HumanInputResponseContinuationWakeStatus.Submitted),
            4,
            new HumanInputResponseContinuationFixedTimeProvider(default));

        await Assert.ThrowsAsync<OperationCanceledException>(() => cancelled.RunOnceAsync(GovernedLoopLocalWorkFamily.HumanInput, cancellation.Token));
        var unavailable = await unavailableClock.RunOnceAsync(GovernedLoopLocalWorkFamily.HumanInput);
        var corrupt = await corruptClock.RunOnceAsync(GovernedLoopLocalWorkFamily.HumanInput);

        Assert.Equal(GovernedLoopLocalWorkResultStatus.Unavailable, unavailable?.Status);
        Assert.Equal(GovernedLoopLocalWorkResultStatus.Corrupt, corrupt?.Status);
        Assert.Empty(source.Cursors);
    }

    [Fact]
    public async Task Healthy_empty_canonical_policy_source_is_probed_before_a_clean_worker_outcome_advertises_executable()
    {
        var policySource = HealthyPolicySource();
        var runner = Runner(
            new HumanInputResponseContinuationRecordingCandidateSource(Page([], null)),
            new HumanInputResponseContinuationRecordingWakePort(HumanInputResponseContinuationWakeStatus.NoWork),
            policySource);

        var result = await runner.RunOnceAsync(GovernedLoopLocalWorkFamily.HumanInput);

        Assert.Equal(GovernedLoopLocalWorkResultStatus.Empty, result?.Status);
        Assert.True(runner.IsExecutable);
        var reference = Assert.Single(policySource.References);
        Assert.Equal("human-input-source-health", reference.PolicyId);
        Assert.Equal("revision-one", reference.RevisionId);
    }

    [Fact]
    public async Task Unavailable_corrupt_malformed_or_throwing_policy_source_evidence_fails_closed_before_recovery_reads()
    {
        var unavailableSource = new HumanInputResponseContinuationSequencePolicySource(
            (_, _) => Task.FromResult(new HumanInputPolicySourceReadResult(HumanInputPolicySourceReadStatus.Unavailable, null, 0)));
        var corruptSource = new HumanInputResponseContinuationSequencePolicySource(
            (_, _) => Task.FromResult(new HumanInputPolicySourceReadResult(HumanInputPolicySourceReadStatus.Unknown, null, 0)));
        var malformedSource = new HumanInputResponseContinuationSequencePolicySource(
            (_, _) => Task.FromResult(new HumanInputPolicySourceReadResult(HumanInputPolicySourceReadStatus.NotFound, null, -1)));
        var throwingSource = new HumanInputResponseContinuationSequencePolicySource(
            (_, _) => Task.FromException<HumanInputPolicySourceReadResult>(new IOException("policy source unavailable")));
        var unavailableRecovery = new HumanInputResponseContinuationRecordingCandidateSource(Page([], null));
        var corruptRecovery = new HumanInputResponseContinuationRecordingCandidateSource(Page([], null));
        var malformedRecovery = new HumanInputResponseContinuationRecordingCandidateSource(Page([], null));
        var throwingRecovery = new HumanInputResponseContinuationRecordingCandidateSource(Page([], null));

        var unavailable = await Runner(unavailableRecovery, new HumanInputResponseContinuationRecordingWakePort(HumanInputResponseContinuationWakeStatus.NoWork), unavailableSource)
            .RunOnceAsync(GovernedLoopLocalWorkFamily.HumanInput);
        var corrupt = await Runner(corruptRecovery, new HumanInputResponseContinuationRecordingWakePort(HumanInputResponseContinuationWakeStatus.NoWork), corruptSource)
            .RunOnceAsync(GovernedLoopLocalWorkFamily.HumanInput);
        var malformed = await Runner(malformedRecovery, new HumanInputResponseContinuationRecordingWakePort(HumanInputResponseContinuationWakeStatus.NoWork), malformedSource)
            .RunOnceAsync(GovernedLoopLocalWorkFamily.HumanInput);
        var faulted = await Runner(throwingRecovery, new HumanInputResponseContinuationRecordingWakePort(HumanInputResponseContinuationWakeStatus.NoWork), throwingSource)
            .RunOnceAsync(GovernedLoopLocalWorkFamily.HumanInput);

        Assert.Equal(GovernedLoopLocalWorkResultStatus.Unavailable, unavailable?.Status);
        Assert.All([corrupt, malformed], result => Assert.Equal(GovernedLoopLocalWorkResultStatus.Corrupt, result?.Status));
        Assert.Equal(GovernedLoopLocalWorkResultStatus.Unavailable, faulted?.Status);
        Assert.All([unavailableRecovery, corruptRecovery, malformedRecovery, throwingRecovery], source => Assert.Empty(source.Cursors));
    }

    [Fact]
    public async Task Only_a_well_formed_exact_and_valid_ready_policy_probe_is_healthy()
    {
        var healthyReady = new HumanInputResponseContinuationSequencePolicySource(ReadyPolicyProbe);
        var divergentReady = new HumanInputResponseContinuationSequencePolicySource(
            (reference, _) => Task.FromResult(new HumanInputPolicySourceReadResult(
                HumanInputPolicySourceReadStatus.Ready,
                Policy(reference with { RevisionId = "revision-two" }),
                1)));
        var healthyRunner = Runner(
            new HumanInputResponseContinuationRecordingCandidateSource(Page([], null)),
            new HumanInputResponseContinuationRecordingWakePort(HumanInputResponseContinuationWakeStatus.NoWork),
            healthyReady);
        var divergentRunner = Runner(
            new HumanInputResponseContinuationRecordingCandidateSource(Page([], null)),
            new HumanInputResponseContinuationRecordingWakePort(HumanInputResponseContinuationWakeStatus.NoWork),
            divergentReady);

        var healthy = await healthyRunner.RunOnceAsync(GovernedLoopLocalWorkFamily.HumanInput);
        var divergent = await divergentRunner.RunOnceAsync(GovernedLoopLocalWorkFamily.HumanInput);

        Assert.Equal(GovernedLoopLocalWorkResultStatus.Empty, healthy?.Status);
        Assert.True(healthyRunner.IsExecutable);
        Assert.Equal(GovernedLoopLocalWorkResultStatus.Corrupt, divergent?.Status);
        Assert.False(divergentRunner.IsExecutable);
    }

    [Fact]
    public async Task Concurrent_human_input_calls_observe_readiness_in_serialized_result_order()
    {
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowFirst = new TaskCompletionSource<HumanInputPolicySourceReadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var policySource = new HumanInputResponseContinuationSequencePolicySource(
            async (_, _) =>
            {
                firstEntered.SetResult();
                return await allowFirst.Task.ConfigureAwait(false);
            },
            (_, _) => Task.FromResult(new HumanInputPolicySourceReadResult(HumanInputPolicySourceReadStatus.Unavailable, null, 0)));
        var runner = Runner(
            new HumanInputResponseContinuationRecordingCandidateSource(Page([], null)),
            new HumanInputResponseContinuationRecordingWakePort(HumanInputResponseContinuationWakeStatus.NoWork),
            policySource);

        var first = runner.RunOnceAsync(GovernedLoopLocalWorkFamily.HumanInput);
        await firstEntered.Task;
        var second = runner.RunOnceAsync(GovernedLoopLocalWorkFamily.HumanInput);
        allowFirst.SetResult(new HumanInputPolicySourceReadResult(HumanInputPolicySourceReadStatus.NotFound, null, 0));

        var firstResult = await first;
        var secondResult = await second;

        Assert.Equal(GovernedLoopLocalWorkResultStatus.Empty, firstResult?.Status);
        Assert.Equal(GovernedLoopLocalWorkResultStatus.Unavailable, secondResult?.Status);
        Assert.False(runner.IsExecutable);
        Assert.Equal(2, policySource.References.Count);
    }

    [Fact]
    public async Task Executable_posture_starts_false_requires_a_clean_probe_and_clears_for_later_corrupt_or_unavailable_evidence()
    {
        var source = new HumanInputResponseContinuationRecordingCandidateSource(
            Page([], null),
            new HumanInputResponseContinuationRecoveryPage(
                HumanInputResponseContinuationRecoveryPageStatus.Current,
                [Candidate("corrupt")],
                null,
                false),
            Page([], null),
            new HumanInputResponseContinuationRecoveryPage(
                HumanInputResponseContinuationRecoveryPageStatus.Unavailable,
                [],
                null,
                false));
        var runner = Runner(source, new HumanInputResponseContinuationRecordingWakePort(HumanInputResponseContinuationWakeStatus.NoWork));

        Assert.False(runner.IsExecutable);
        Assert.Equal(GovernedLoopLocalWorkResultStatus.Empty, (await runner.RunOnceAsync(GovernedLoopLocalWorkFamily.HumanInput))?.Status);
        Assert.True(runner.IsExecutable);
        Assert.Equal(GovernedLoopLocalWorkResultStatus.Corrupt, (await runner.RunOnceAsync(GovernedLoopLocalWorkFamily.HumanInput))?.Status);
        Assert.False(runner.IsExecutable);
        Assert.Equal(GovernedLoopLocalWorkResultStatus.Empty, (await runner.RunOnceAsync(GovernedLoopLocalWorkFamily.HumanInput))?.Status);
        Assert.True(runner.IsExecutable);
        Assert.Equal(GovernedLoopLocalWorkResultStatus.Unavailable, (await runner.RunOnceAsync(GovernedLoopLocalWorkFamily.HumanInput))?.Status);
        Assert.False(runner.IsExecutable);
    }

    [Fact]
    public async Task Missing_or_throwing_recovery_evidence_never_advertises_executable_and_caller_cancellation_preserves_prior_readiness()
    {
        var missing = new HumanInputResponseContinuationMissingCandidateSource();
        var missingRunner = new HumanInputResponseContinuationWorkRunner(
            new HumanInputResponseContinuationRecordingLocalWorkRunner(),
            missing,
            HealthyPolicySource(),
            new HumanInputResponseContinuationRecordingWakePort(HumanInputResponseContinuationWakeStatus.NoWork),
            4,
            new HumanInputResponseContinuationFixedTimeProvider(_now));
        var throwingRunner = new HumanInputResponseContinuationWorkRunner(
            new HumanInputResponseContinuationRecordingLocalWorkRunner(),
            new HumanInputResponseContinuationThrowingCandidateSource(),
            HealthyPolicySource(),
            new HumanInputResponseContinuationRecordingWakePort(HumanInputResponseContinuationWakeStatus.NoWork),
            4,
            new HumanInputResponseContinuationFixedTimeProvider(_now));
        var readyRunner = Runner(
            new HumanInputResponseContinuationRecordingCandidateSource(Page([], null)),
            new HumanInputResponseContinuationRecordingWakePort(HumanInputResponseContinuationWakeStatus.NoWork));
        using var cancellation = new CancellationTokenSource();

        Assert.Equal(GovernedLoopLocalWorkResultStatus.Corrupt, (await missingRunner.RunOnceAsync(GovernedLoopLocalWorkFamily.HumanInput))?.Status);
        Assert.False(missingRunner.IsExecutable);
        Assert.Equal(GovernedLoopLocalWorkResultStatus.Unavailable, (await throwingRunner.RunOnceAsync(GovernedLoopLocalWorkFamily.HumanInput))?.Status);
        Assert.False(throwingRunner.IsExecutable);
        Assert.Equal(GovernedLoopLocalWorkResultStatus.Empty, (await readyRunner.RunOnceAsync(GovernedLoopLocalWorkFamily.HumanInput))?.Status);
        Assert.True(readyRunner.IsExecutable);

        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => readyRunner.RunOnceAsync(GovernedLoopLocalWorkFamily.HumanInput, cancellation.Token));

        Assert.True(readyRunner.IsExecutable);
    }

    private static HumanInputResponseContinuationCandidate Candidate(string suffix)
        => new("run-" + suffix, "checkpoint-" + suffix);

    private static HumanInputResponseContinuationRecoveryPage Page(
        IReadOnlyList<HumanInputResponseContinuationCandidate> candidates,
        string? cursor)
        => new(HumanInputResponseContinuationRecoveryPageStatus.Current, candidates, cursor, cursor is not null);

    private static HumanInputResponseContinuationWorkRunner Runner(
        HumanInputResponseContinuationRecordingCandidateSource source,
        HumanInputResponseContinuationRecordingWakePort continuation,
        HumanInputResponseContinuationSequencePolicySource? policySource = null)
        => new(
            new HumanInputResponseContinuationRecordingLocalWorkRunner(),
            source,
            policySource ?? HealthyPolicySource(),
            continuation,
            4,
            new HumanInputResponseContinuationFixedTimeProvider(_now));

    private static HumanInputResponseContinuationSequencePolicySource HealthyPolicySource()
        => new((_, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HumanInputPolicySourceReadResult(HumanInputPolicySourceReadStatus.NotFound, null, 0));
        });

    private static Task<HumanInputPolicySourceReadResult> ReadyPolicyProbe(
        HumanInputPolicyReference reference,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new HumanInputPolicySourceReadResult(HumanInputPolicySourceReadStatus.Ready, Policy(reference), 1));
    }

    private static HumanInputPolicyArtifact Policy(HumanInputPolicyReference reference)
        => HumanInputPolicyArtifactHash.Apply(new HumanInputPolicyArtifact(
            HumanInputPolicyArtifact.CurrentSchemaVersion,
            reference.PolicyId,
            reference.RevisionId,
            HumanInputPolicyKind.ResponseWindow,
            "workspace-sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "graph-one",
            "actor-one",
            3_600_000,
            HumanInputTerminalDisposition.Unknown,
            string.Empty));
}
