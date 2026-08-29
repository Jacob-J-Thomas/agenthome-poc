using System.Text;
using System.Text.Json;
using EmbodySense.Core.Application.HumanInput.Continuations.Models;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Persistence.HumanInput.Continuations;

namespace EmbodySense.Core.Persistence.Tests.HumanInput.Continuations;

public sealed class HumanInputResponseContinuationRecoveryStoreTests
{
    [Fact]
    public async Task One_checkpoint_scan_is_bounded_mid_run_resumable_no_wrap_and_restarts_only_after_an_empty_tail()
    {
        var context = HumanInputResponseContinuationRecoveryFixture.CreateWaitingContext();
        var source = new ScriptedRunStore(context.Run);
        var recovery = new HumanInputResponseContinuationRecoveryStore(source);

        var first = await recovery.ListCandidatesAsync(1, null, HumanInputResponseContinuationRecoveryFixture.Now);
        var midRun = await recovery.ListCandidatesAsync(1, first.NextScanCursor, HumanInputResponseContinuationRecoveryFixture.Now);
        var tail = await recovery.ListCandidatesAsync(1, midRun.NextScanCursor, HumanInputResponseContinuationRecoveryFixture.Now);
        var fresh = await recovery.ListCandidatesAsync(1, tail.NextScanCursor, HumanInputResponseContinuationRecoveryFixture.Now);

        Assert.Equal(HumanInputResponseContinuationRecoveryPageStatus.Current, first.Status);
        Assert.Equal([(context.Run.Id, context.Checkpoint.Binding.CheckpointId)], first.Candidates.Select(candidate => (candidate.RunId, candidate.CheckpointId)));
        Assert.True(first.HasMoreScanWork);
        Assert.False(string.IsNullOrWhiteSpace(first.NextScanCursor));
        Assert.Empty(midRun.Candidates);
        Assert.True(midRun.HasMoreScanWork);
        Assert.False(string.IsNullOrWhiteSpace(midRun.NextScanCursor));
        Assert.Empty(tail.Candidates);
        Assert.False(tail.HasMoreScanWork);
        Assert.Null(tail.NextScanCursor);
        Assert.Equal([(context.Run.Id, context.Checkpoint.Binding.CheckpointId)], fresh.Candidates.Select(candidate => (candidate.RunId, candidate.CheckpointId)));
        Assert.Equal(3, source.ListPageCount);
        Assert.Equal(3, source.GetCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Invalid_maximum_count_is_closed_without_reading_canonical_state(int maximumCount)
    {
        var context = HumanInputResponseContinuationRecoveryFixture.CreateWaitingContext();
        var source = new ScriptedRunStore(context.Run);

        var page = await new HumanInputResponseContinuationRecoveryStore(source).ListCandidatesAsync(maximumCount, null, HumanInputResponseContinuationRecoveryFixture.Now);

        Assert.Equal(HumanInputResponseContinuationRecoveryPageStatus.Invalid, page.Status);
        Assert.Empty(page.Candidates);
        Assert.Equal(0, source.ListPageCount);
        Assert.Equal(0, source.GetCount);
    }

    [Fact]
    public async Task Accepted_answered_not_resumed_checkpoint_is_recovered_once_from_its_current_canonical_run()
    {
        var context = HumanInputResponseContinuationRecoveryFixture.CreateWaitingContext();
        var answered = HumanInputResponseContinuationRecoveryFixture.AnsweredNotResumed(context);
        var source = new ScriptedRunStore(answered);

        var page = await new HumanInputResponseContinuationRecoveryStore(source).ListCandidatesAsync(
            1,
            null,
            HumanInputResponseContinuationRecoveryFixture.Now.AddMinutes(2));

        Assert.Equal(HumanInputResponseContinuationRecoveryPageStatus.Current, page.Status);
        Assert.Equal([(answered.Id, answered.HumanInputWaitingCheckpoints[0].Binding.CheckpointId)], page.Candidates.Select(candidate => (candidate.RunId, candidate.CheckpointId)));
        Assert.True(page.HasMoreScanWork);
    }

    [Fact]
    public async Task Active_parallel_frontier_recovers_its_pending_human_input_publication_candidate()
    {
        var context = HumanInputResponseContinuationRecoveryFixture.CreateActivePendingContext();
        var source = new ScriptedRunStore(context.Run);

        var page = await new HumanInputResponseContinuationRecoveryStore(source).ListCandidatesAsync(
            1,
            null,
            HumanInputResponseContinuationRecoveryFixture.Now.AddMinutes(1));

        Assert.Equal(CustomLoopRunStatus.Running, context.Run.Status);
        Assert.Equal(GovernedLoopFrontierStatus.Active, context.Run.Frontier?.Payload.Status);
        var candidate = Assert.Single(page.Candidates);
        Assert.Equal(context.Run.Id, candidate.RunId);
        Assert.Equal(context.Checkpoint.Binding.CheckpointId, candidate.CheckpointId);
        Assert.Equal(context.Checkpoint.CheckpointHash, candidate.CheckpointHash);
    }

    [Fact]
    public async Task Ordered_multiple_runs_have_no_overlap_and_a_lower_key_append_is_seen_only_after_the_empty_tail_resets_the_sweep()
    {
        var first = HumanInputResponseContinuationRecoveryFixture.CreateWaitingContext("continuation-run-a");
        var later = HumanInputResponseContinuationRecoveryFixture.CreateWaitingContext("continuation-run-z");
        var appendedLower = HumanInputResponseContinuationRecoveryFixture.CreateWaitingContext("continuation-run-0");
        var source = new OrderedRunStore(
            [first.Run, later.Run, appendedLower.Run],
            [first.Run],
            [later.Run],
            [],
            [appendedLower.Run]);
        var recovery = new HumanInputResponseContinuationRecoveryStore(source);

        var firstPage = await recovery.ListCandidatesAsync(1, null, HumanInputResponseContinuationRecoveryFixture.Now);
        var afterFirst = await recovery.ListCandidatesAsync(1, firstPage.NextScanCursor, HumanInputResponseContinuationRecoveryFixture.Now);
        var laterPage = await recovery.ListCandidatesAsync(1, afterFirst.NextScanCursor, HumanInputResponseContinuationRecoveryFixture.Now);
        var afterLater = await recovery.ListCandidatesAsync(1, laterPage.NextScanCursor, HumanInputResponseContinuationRecoveryFixture.Now);
        var tail = await recovery.ListCandidatesAsync(1, afterLater.NextScanCursor, HumanInputResponseContinuationRecoveryFixture.Now);
        var lowerPage = await recovery.ListCandidatesAsync(1, tail.NextScanCursor, HumanInputResponseContinuationRecoveryFixture.Now);

        Assert.Equal([first.Run.Id], firstPage.Candidates.Select(candidate => candidate.RunId));
        Assert.Equal([later.Run.Id], laterPage.Candidates.Select(candidate => candidate.RunId));
        Assert.Empty(afterFirst.Candidates);
        Assert.Empty(afterLater.Candidates);
        Assert.Null(tail.NextScanCursor);
        Assert.Equal([appendedLower.Run.Id], lowerPage.Candidates.Select(candidate => candidate.RunId));
        Assert.Equal(4, source.ListPageCount);
        Assert.Equal(5, source.GetCount);
    }

    [Fact]
    public async Task Noncanonical_tampered_and_forward_cursors_fail_closed_without_emitting_stale_candidates()
    {
        var context = HumanInputResponseContinuationRecoveryFixture.CreateWaitingContext();
        var source = new ScriptedRunStore(context.Run);
        var recovery = new HumanInputResponseContinuationRecoveryStore(source);
        var first = await recovery.ListCandidatesAsync(1, null, HumanInputResponseContinuationRecoveryFixture.Now);
        var cursors = new[]
        {
            first.NextScanCursor + "=",
            NoncanonicalCursor(context.Run.Id, context.Run.CreatedAtUtc.UtcTicks, 1),
            Cursor(context.Run.Id, context.Run.CreatedAtUtc.UtcTicks, 2),
        };

        foreach (var cursor in cursors)
        {
            var page = await recovery.ListCandidatesAsync(1, cursor, HumanInputResponseContinuationRecoveryFixture.Now);

            Assert.Equal(HumanInputResponseContinuationRecoveryPageStatus.Invalid, page.Status);
            Assert.Empty(page.Candidates);
            Assert.Null(page.NextScanCursor);
            Assert.False(page.HasMoreScanWork);
        }

        Assert.Equal(1, source.ListPageCount);
        // The initial discovery and a structurally valid forward cursor each read the run once.
        // The padded and noncanonical cursors are rejected by the cursor codec before that read.
        Assert.Equal(2, source.GetCount);
    }

    [Fact]
    public async Task Run_deletion_change_corruption_and_unavailability_never_create_a_candidate_from_stale_cursor_state()
    {
        var context = HumanInputResponseContinuationRecoveryFixture.CreateWaitingContext();
        var source = new ScriptedRunStore(context.Run);
        var recovery = new HumanInputResponseContinuationRecoveryStore(source);
        var first = await recovery.ListCandidatesAsync(1, null, HumanInputResponseContinuationRecoveryFixture.Now);

        source.Run = null;
        var deleted = await recovery.ListCandidatesAsync(1, first.NextScanCursor, HumanInputResponseContinuationRecoveryFixture.Now);
        source.Run = context.Run with { CreatedAtUtc = context.Run.CreatedAtUtc.AddTicks(1) };
        var changed = await recovery.ListCandidatesAsync(1, first.NextScanCursor, HumanInputResponseContinuationRecoveryFixture.Now);
        source.Run = context.Run with { HumanInputWaitingCheckpoints = [] };
        var corrupt = await recovery.ListCandidatesAsync(1, first.NextScanCursor, HumanInputResponseContinuationRecoveryFixture.Now);
        source.GetException = new IOException("Canonical store unavailable.");
        var unavailable = await recovery.ListCandidatesAsync(1, first.NextScanCursor, HumanInputResponseContinuationRecoveryFixture.Now);

        Assert.Equal(HumanInputResponseContinuationRecoveryPageStatus.Current, deleted.Status);
        Assert.Empty(deleted.Candidates);
        Assert.True(deleted.HasMoreScanWork);
        Assert.Equal(HumanInputResponseContinuationRecoveryPageStatus.Invalid, changed.Status);
        Assert.Empty(changed.Candidates);
        Assert.Equal(HumanInputResponseContinuationRecoveryPageStatus.Invalid, corrupt.Status);
        Assert.Empty(corrupt.Candidates);
        Assert.Equal(HumanInputResponseContinuationRecoveryPageStatus.Unavailable, unavailable.Status);
        Assert.Empty(unavailable.Candidates);
    }

    [Fact]
    public async Task Source_page_shape_corruption_and_unavailability_are_closed_without_candidates()
    {
        var context = HumanInputResponseContinuationRecoveryFixture.CreateWaitingContext();
        var source = new ScriptedRunStore(context.Run)
        {
            PageOverride = new CustomLoopRunPage([null!], null),
        };
        var malformed = await new HumanInputResponseContinuationRecoveryStore(source).ListCandidatesAsync(1, null, HumanInputResponseContinuationRecoveryFixture.Now);
        source.PageOverride = null;
        source.ListPageException = new IOException("Canonical source is unavailable.");
        var unavailable = await new HumanInputResponseContinuationRecoveryStore(source).ListCandidatesAsync(1, null, HumanInputResponseContinuationRecoveryFixture.Now);

        Assert.Equal(HumanInputResponseContinuationRecoveryPageStatus.Invalid, malformed.Status);
        Assert.Empty(malformed.Candidates);
        Assert.Equal(HumanInputResponseContinuationRecoveryPageStatus.Unavailable, unavailable.Status);
        Assert.Empty(unavailable.Candidates);
    }

    [Fact]
    public async Task Cancellation_propagates_from_the_canonical_page_read()
    {
        var context = HumanInputResponseContinuationRecoveryFixture.CreateWaitingContext();
        var source = new ScriptedRunStore(context.Run);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new HumanInputResponseContinuationRecoveryStore(source).ListCandidatesAsync(
            1,
            null,
            HumanInputResponseContinuationRecoveryFixture.Now,
            cancellation.Token));
    }

    private static string Cursor(string runId, long createdAtUtcTicks, int nextCheckpointOrdinal)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new RecoveryCursor(1, null, runId, createdAtUtcTicks, nextCheckpointOrdinal),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return Convert.ToBase64String(payload).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string NoncanonicalCursor(string runId, long createdAtUtcTicks, int nextCheckpointOrdinal)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{{\"schemaVersion\": 1, \"afterRunCursor\": null, \"resumeRunId\": \"{runId}\", \"resumeRunCreatedAtUtcTicks\": {createdAtUtcTicks}, \"nextCheckpointOrdinal\": {nextCheckpointOrdinal}}}"))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

}
