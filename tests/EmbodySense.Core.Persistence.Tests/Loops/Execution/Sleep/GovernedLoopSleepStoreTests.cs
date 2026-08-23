using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using EmbodySense.Core.Application.Loops.Posture.Models;
using EmbodySense.Core.Application.Loops.Sleep;
using EmbodySense.Core.Application.Loops.Sleep.Models;
using EmbodySense.Core.Application.Tests.Loops.Sleep;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Execution.Sleep;
using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;
using EmbodySense.Core.Common.Loops.Posture;
using EmbodySense.Core.Common.Tests.Loops.Execution.Sleep;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops.Execution.Sleep;
using EmbodySense.Core.Persistence.Loops.Execution.Sleep.Models;
using EmbodySense.Core.Persistence.Triggers.Schedules;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Persistence.Tests.Loops.Execution.Sleep;

public sealed class GovernedLoopSleepStoreTests
{
    private const string CrossProcessWorkspace = "EMBODYSENSE_SLEEP_STORE_WORKSPACE";
    private const string CrossProcessGate = "EMBODYSENSE_SLEEP_STORE_GATE";
    private const string CrossProcessReady = "EMBODYSENSE_SLEEP_STORE_READY";
    private const string CrossProcessOutput = "EMBODYSENSE_SLEEP_STORE_OUTPUT";
    private const string CrossProcessCrashBoundary = "EMBODYSENSE_SLEEP_STORE_CRASH_BOUNDARY";
    private const string CrossProcessOperation = "EMBODYSENSE_SLEEP_STORE_OPERATION";

    [Fact]
    public async Task Operational_pages_are_deterministic_cursor_safe_detached_and_validate_the_whole_catalog_before_projection()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new GovernedLoopSleepStore(paths);
        var checkpoints = new[]
        {
            GovernedLoopSleepContractTestFixture.TimestampCheckpoint(binding: GovernedLoopSleepContractTestFixture.Binding(runId: "run-c")),
            GovernedLoopSleepContractTestFixture.TimestampCheckpoint(binding: GovernedLoopSleepContractTestFixture.Binding(runId: "run-a")),
            GovernedLoopSleepContractTestFixture.TimestampCheckpoint(binding: GovernedLoopSleepContractTestFixture.Binding(runId: "run-b"))
        };
        foreach (var checkpoint in checkpoints)
        {
            Assert.Equal(
                GovernedLoopSleepCheckpointMutationStatus.Committed,
                (await store.PublishAndReleaseAsync(checkpoint, GovernedLoopSleepContractTestFixture.Hash('9')))!.Status);
        }
        var expected = checkpoints.OrderBy(item => item.CheckpointId, StringComparer.Ordinal).ToArray();

        var first = await store.ReadAsync(new GovernedLoopOperationalEvidencePageRequest(1));
        var firstAgain = await store.ReadAsync(new GovernedLoopOperationalEvidencePageRequest(1));
        var second = await store.ReadAsync(new GovernedLoopOperationalEvidencePageRequest(1, first.ContinuationCursor));
        var third = await store.ReadAsync(new GovernedLoopOperationalEvidencePageRequest(1, second.ContinuationCursor));
        var nonexistent = await store.ReadAsync(new GovernedLoopOperationalEvidencePageRequest(1, "0"));
        var beyondTail = await store.ReadAsync(new GovernedLoopOperationalEvidencePageRequest(1, "z"));
        var malformed = await store.ReadAsync(new GovernedLoopOperationalEvidencePageRequest(1, "bad cursor"));

        Assert.Equal(expected[0].CheckpointId, Assert.Single(first.Items).Checkpoint.CheckpointId);
        Assert.Equal(expected[0].CheckpointId, first.ContinuationCursor);
        Assert.Equal(expected[1].CheckpointId, Assert.Single(second.Items).Checkpoint.CheckpointId);
        Assert.Equal(expected[1].CheckpointId, second.ContinuationCursor);
        Assert.Equal(expected[2].CheckpointId, Assert.Single(third.Items).Checkpoint.CheckpointId);
        Assert.False(third.HasMore);
        Assert.Null(third.ContinuationCursor);
        Assert.Equal(expected[0].CheckpointId, Assert.Single(nonexistent.Items).Checkpoint.CheckpointId);
        Assert.Equal(GovernedLoopOperationalEvidenceReadStatus.Empty, beyondTail.Status);
        Assert.Equal(GovernedLoopOperationalEvidenceReadStatus.Corrupt, malformed.Status);
        Assert.NotSame(first.Items[0].Checkpoint, firstAgain.Items[0].Checkpoint);
        Assert.NotSame(first.Items[0].Checkpoint.Binding, firstAgain.Items[0].Checkpoint.Binding);
        var exposed = Assert.IsAssignableFrom<IList<GovernedLoopWakeEvidenceSnapshot>>(first.Items);
        Assert.Throws<NotSupportedException>(() => exposed.Add(first.Items[0]));

        await File.WriteAllTextAsync(Assert.Single(Directory.EnumerateFiles(StoreRoot(paths), "ledger-*.json")), "{}");
        var corruptOffPage = await store.ReadAsync(new GovernedLoopOperationalEvidencePageRequest(1, "z"));
        Assert.Equal(GovernedLoopOperationalEvidenceReadStatus.Corrupt, corruptOffPage.Status);
    }

    [Fact]
    public async Task Publish_read_restart_and_exact_retry_preserve_immutable_checkpoint_and_posture_fence()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var checkpoint = GovernedLoopSleepContractTestFixture.EventCheckpoint();
        var postureHash = GovernedLoopSleepContractTestFixture.Hash('9');

        var published = await new GovernedLoopSleepStore(paths).PublishAndReleaseAsync(checkpoint, postureHash);
        var read = await new GovernedLoopSleepStore(paths).ReadCheckpointAsync(checkpoint.CheckpointId);
        var replay = await new GovernedLoopSleepStore(paths).PublishAndReleaseAsync(checkpoint, postureHash);
        var mismatchedPosture = await new GovernedLoopSleepStore(paths)
            .PublishAndReleaseAsync(checkpoint, GovernedLoopSleepContractTestFixture.Hash('8'));

        Assert.Equal(GovernedLoopSleepCheckpointMutationStatus.Committed, published!.Status);
        Assert.Equal(checkpoint, published.Checkpoint);
        Assert.NotSame(checkpoint, published.Checkpoint);
        Assert.NotSame(checkpoint.Binding, published.Checkpoint!.Binding);
        Assert.Equal(GovernedLoopSleepStoreReadStatus.Found, read!.Status);
        Assert.Equal(checkpoint, read.Checkpoint);
        Assert.NotSame(published.Checkpoint, read.Checkpoint);
        Assert.Equal(GovernedLoopSleepCheckpointMutationStatus.Replayed, replay!.Status);
        Assert.Equal(GovernedLoopSleepCheckpointMutationStatus.Conflict, mismatchedPosture!.Status);
        Assert.Null(mismatchedPosture.Checkpoint);
        Assert.Single(Directory.EnumerateFiles(StoreRoot(paths), "ledger-*.json"));
    }

    [Fact]
    public async Task Reconstructed_publication_replays_original_checkpoint_without_advancing_catalog_generation()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var postureHash = GovernedLoopSleepContractTestFixture.Hash('9');
        var original = GovernedLoopSleepContractTestFixture.TimestampCheckpoint();
        var reconstructed = GovernedLoopSleepContractTestFixture.TimestampCheckpoint(
            publishedAtUtc: original.PublishedAtUtc.AddMinutes(5));
        var first = new GovernedLoopSleepStore(paths);
        var second = new GovernedLoopSleepStore(paths);

        var committed = await first.PublishAndReleaseAsync(original, postureHash);
        var ledgerPath = Assert.Single(Directory.EnumerateFiles(StoreRoot(paths), "ledger-*.json"));
        var committedBytes = await File.ReadAllBytesAsync(ledgerPath);

        var replayed = await second.PublishAndReleaseAsync(reconstructed, postureHash);
        var changedPosture = await second.PublishAndReleaseAsync(
            reconstructed,
            GovernedLoopSleepContractTestFixture.Hash('8'));
        var changedWakeCondition = original with
        {
            WakeDeadlineUtc = original.WakeDeadlineUtc!.Value.AddMinutes(1),
            ContentHash = string.Empty,
        };
        changedWakeCondition = changedWakeCondition with
        {
            ContentHash = GovernedLoopSleepContractHash.Compute(changedWakeCondition),
        };
        var changedWake = await second.PublishAndReleaseAsync(changedWakeCondition, postureHash);

        Assert.Equal(original.CheckpointId, reconstructed.CheckpointId);
        Assert.NotEqual(original.ContentHash, reconstructed.ContentHash);
        Assert.Equal(GovernedLoopSleepCheckpointMutationStatus.Committed, committed!.Status);
        Assert.Equal(GovernedLoopSleepCheckpointMutationStatus.Replayed, replayed!.Status);
        Assert.Equal(original, replayed.Checkpoint);
        Assert.NotEqual(reconstructed.PublishedAtUtc, replayed.Checkpoint!.PublishedAtUtc);
        Assert.Equal(GovernedLoopSleepCheckpointMutationStatus.Conflict, changedPosture!.Status);
        Assert.Null(changedPosture.Checkpoint);
        Assert.Equal(GovernedLoopSleepCheckpointMutationStatus.Conflict, changedWake!.Status);
        Assert.Null(changedWake.Checkpoint);
        Assert.Equal(committedBytes, await File.ReadAllBytesAsync(ledgerPath));
        Assert.Single(Directory.EnumerateFiles(StoreRoot(paths), "ledger-*.json"));
    }

    [Fact]
    public async Task Checkpoint_replay_rejects_trusted_time_rollback_without_replacing_original_evidence()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var postureHash = GovernedLoopSleepContractTestFixture.Hash('9');
        var original = GovernedLoopSleepContractTestFixture.TimestampCheckpoint();
        var rolledBack = GovernedLoopSleepContractHash.Apply(original with
        {
            PublishedAtUtc = original.PublishedAtUtc.AddTicks(-1),
            ContentHash = string.Empty
        });
        var store = new GovernedLoopSleepStore(paths);

        var published = await store.PublishAndReleaseAsync(original, postureHash);
        var replay = await store.PublishAndReleaseAsync(rolledBack, postureHash);
        var retained = await store.ReadCheckpointAsync(original.CheckpointId);

        Assert.Equal(GovernedLoopSleepCheckpointMutationStatus.Committed, published!.Status);
        Assert.Equal(GovernedLoopSleepCheckpointMutationStatus.Conflict, replay!.Status);
        Assert.Equal(original, retained!.Checkpoint);
    }

    [Fact]
    public async Task Missing_and_malformed_identities_return_closed_read_results_without_filesystem_access()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new GovernedLoopSleepStore(paths);

        var missingCheckpoint = await store.ReadCheckpointAsync(GovernedLoopSleepContractTestFixture.Hash('1'));
        var missingWake = await store.ReadWakeAsync(GovernedLoopSleepContractTestFixture.Hash('2'));
        var malformedCheckpoint = await store.ReadCheckpointAsync("not-a-hash");
        var malformedWake = await store.ReadWakeAsync(new string('A', 64));

        Assert.Equal(GovernedLoopSleepStoreReadStatus.NotFound, missingCheckpoint!.Status);
        Assert.Equal(GovernedLoopSleepStoreReadStatus.NotFound, missingWake!.Status);
        Assert.Equal(GovernedLoopSleepStoreReadStatus.Conflict, malformedCheckpoint!.Status);
        Assert.Equal(GovernedLoopSleepStoreReadStatus.Conflict, malformedWake!.Status);
        Assert.Null(malformedCheckpoint.Checkpoint);
        Assert.Null(malformedWake.Evidence);
    }

    [Fact]
    public async Task Wake_claim_is_exactly_once_replayable_and_fenced_to_checkpoint_and_posture()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new GovernedLoopSleepStore(paths);
        var checkpoint = GovernedLoopSleepContractTestFixture.EventCheckpoint();
        var postureHash = GovernedLoopSleepContractTestFixture.Hash('9');
        var identity = GovernedLoopSleepContractTestFixture.WakeIdentity(checkpoint);
        var prepared = GovernedLoopSleepContractTestFixture.WakeEvidence(identity: identity);
        Assert.Equal(GovernedLoopSleepCheckpointMutationStatus.Committed, (await store.PublishAndReleaseAsync(checkpoint, postureHash))!.Status);

        var committed = await store.CreateWakeAsync(checkpoint, prepared, postureHash);
        var read = await new GovernedLoopSleepStore(paths).ReadWakeAsync(identity.WakeId);
        var replay = await new GovernedLoopSleepStore(paths).CreateWakeAsync(checkpoint, prepared, postureHash);
        var substitutedInitial = GovernedLoopSleepContractTestFixture.WakeEvidence(
            identity: identity,
            recordedAtUtc: prepared.RecordedAtUtc.AddTicks(1));
        var substitutedReplay = await store.CreateWakeAsync(checkpoint, substitutedInitial, postureHash);
        var wrongFence = await store.CreateWakeAsync(checkpoint, prepared, GovernedLoopSleepContractTestFixture.Hash('8'));
        var competingIdentity = GovernedLoopSleepContractHash.Apply(identity with
        {
            AuthenticationEvidenceHash = GovernedLoopSleepContractTestFixture.Hash('e'),
            WakeId = string.Empty,
            ContentHash = string.Empty,
        });
        var competing = GovernedLoopSleepContractTestFixture.WakeEvidence(identity: competingIdentity);
        var claimed = await new GovernedLoopSleepStore(paths).CreateWakeAsync(checkpoint, competing, postureHash);

        Assert.Equal(GovernedLoopWakeEvidenceMutationStatus.Committed, committed!.Status);
        Assert.Equal(GovernedLoopSleepStoreReadStatus.Found, read!.Status);
        Assert.Equal(prepared, read.Evidence);
        Assert.Equal(prepared, read.PreparedEvidence);
        Assert.NotSame(prepared, read.Evidence);
        Assert.NotSame(prepared, read.PreparedEvidence);
        Assert.NotSame(prepared.Identity, read.Evidence!.Identity);
        Assert.NotSame(prepared.Identity, read.PreparedEvidence!.Identity);
        Assert.Equal(GovernedLoopWakeEvidenceMutationStatus.Replayed, replay!.Status);
        Assert.Equal(GovernedLoopWakeEvidenceMutationStatus.Conflict, substitutedReplay!.Status);
        Assert.Equal(GovernedLoopWakeEvidenceMutationStatus.Conflict, wrongFence!.Status);
        Assert.Equal(GovernedLoopWakeEvidenceMutationStatus.CheckpointClaimed, claimed!.Status);
        Assert.Equal(prepared, claimed.Evidence);
    }

    [Fact]
    public async Task Real_store_preserves_publication_fence_while_sibling_progress_admits_one_exact_current_posture_wake()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var waiting = GovernedLoopSleepApplicationTestFixture.WaitingNode();
        var publishedPosture = GovernedLoopSleepApplicationTestFixture.Posture(
            node: waiting,
            lifecycleStatus: GovernedLoopRunStatus.Running,
            frontierStatus: GovernedLoopFrontierStatus.Active,
            nodes: [waiting, GovernedLoopSleepApplicationTestFixture.ReadyNode()]);
        var currentPosture = new StubGovernedLoopSleepCurrentPosturePort
        {
            Result = new GovernedLoopSleepCurrentPostureReadResult(
                GovernedLoopSleepCurrentPostureReadStatus.Found,
                publishedPosture)
        };
        var continuation = new StubGovernedLoopWakeContinuationPort();
        var service = new GovernedLoopSleepService(
            new GovernedLoopSleepStore(paths),
            currentPosture,
            continuation,
            new StubGovernedLoopAuthenticatedWakeVerificationPort(),
            new StubGovernedLoopSleepTimeProvider(GovernedLoopSleepApplicationTestFixture.Now));
        var publication = await service.PublishAsync(GovernedLoopSleepApplicationTestFixture.PublicationRequest(publishedPosture));
        var checkpoint = Assert.IsType<GovernedLoopSleepCheckpoint>(publication.Checkpoint);
        var advancedPosture = GovernedLoopSleepApplicationTestFixture.Posture(
            node: waiting,
            frontierVersion: publishedPosture.Execution.Frontier.Payload.FrontierVersion + 1,
            lifecycleStatus: GovernedLoopRunStatus.Running,
            frontierStatus: GovernedLoopFrontierStatus.Active,
            nodes: [waiting, GovernedLoopSleepApplicationTestFixture.RunningNode()]) with
        {
            PostureHash = GovernedLoopSleepApplicationTestFixture.Hash('8')
        };
        currentPosture.Result = new GovernedLoopSleepCurrentPostureReadResult(
            GovernedLoopSleepCurrentPostureReadStatus.Found,
            advancedPosture);
        var request = new GovernedLoopWakeRequest(checkpoint.CheckpointId, checkpoint.ContentHash);

        var first = await service.WakeAsync(request);
        var duplicate = await service.WakeAsync(request);
        var ledger = JsonNode.Parse(await File.ReadAllBytesAsync(LatestLedger(paths)))!.AsObject();
        var entry = ((JsonArray)ledger["entries"]!)[0]!.AsObject();

        Assert.Equal(GovernedLoopSleepPublicationStatus.Published, publication.Status);
        Assert.NotEqual(checkpoint.Binding.FrontierVersion, advancedPosture.Execution.Frontier.Payload.FrontierVersion);
        Assert.Equal(GovernedLoopWakeResultStatus.Committed, first.Status);
        Assert.Equal(GovernedLoopWakeResultStatus.Duplicate, duplicate.Status);
        Assert.Equal(1, continuation.ContinueCount);
        Assert.Equal(publishedPosture.PostureHash, entry["publicationPostureHash"]!.GetValue<string>());
        Assert.Equal(advancedPosture.PostureHash, entry["wakeClaimPostureHash"]!.GetValue<string>());
    }

    [Fact]
    public async Task Whole_wake_state_compare_exchange_is_contiguous_conflict_aware_and_restart_safe()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new GovernedLoopSleepStore(paths);
        var checkpoint = GovernedLoopSleepContractTestFixture.TimestampCheckpoint();
        var postureHash = GovernedLoopSleepContractTestFixture.Hash('9');
        var identity = GovernedLoopSleepContractTestFixture.WakeIdentity(checkpoint);
        var prepared = GovernedLoopSleepContractTestFixture.WakeEvidence(identity: identity);
        var committed = GovernedLoopSleepContractTestFixture.WakeEvidence(
            GovernedLoopWakeDisposition.Committed,
            2,
            identity,
            recordedAtUtc: prepared.RecordedAtUtc.AddSeconds(1));
        var competing = GovernedLoopSleepContractTestFixture.WakeEvidence(
            GovernedLoopWakeDisposition.AmbiguousAttempt,
            2,
            identity,
            dispositionEvidenceReference: "ambiguous-after-call",
            recordedAtUtc: prepared.RecordedAtUtc.AddSeconds(1));
        await store.PublishAndReleaseAsync(checkpoint, postureHash);
        await store.CreateWakeAsync(checkpoint, prepared, postureHash);

        var applied = await store.AdvanceWakeAsync(prepared, committed);
        var replay = await new GovernedLoopSleepStore(paths).AdvanceWakeAsync(prepared, committed);
        var conflict = await new GovernedLoopSleepStore(paths).AdvanceWakeAsync(prepared, competing);
        var initialRetryAfterAdvance = await new GovernedLoopSleepStore(paths).CreateWakeAsync(checkpoint, prepared, postureHash);
        var read = await new GovernedLoopSleepStore(paths).ReadWakeAsync(identity.WakeId);

        Assert.Equal(GovernedLoopWakeEvidenceMutationStatus.Committed, applied!.Status);
        Assert.Equal(GovernedLoopWakeEvidenceMutationStatus.Replayed, replay!.Status);
        Assert.Equal(GovernedLoopWakeEvidenceMutationStatus.Conflict, conflict!.Status);
        Assert.Equal(committed, conflict.Evidence);
        Assert.Equal(GovernedLoopWakeEvidenceMutationStatus.Replayed, initialRetryAfterAdvance!.Status);
        Assert.Equal(committed, initialRetryAfterAdvance.Evidence);
        Assert.Equal(committed, read!.Evidence);
        Assert.Equal(prepared, read.PreparedEvidence);
    }

    [Fact]
    public async Task Wake_transition_ledger_preserves_prepared_and_terminal_evidence_without_duplicate_replay_appends()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var checkpoint = GovernedLoopSleepContractTestFixture.TimestampCheckpoint();
        var postureHash = GovernedLoopSleepContractTestFixture.Hash('9');
        var identity = GovernedLoopSleepContractTestFixture.WakeIdentity(checkpoint);
        var prepared = GovernedLoopSleepContractTestFixture.WakeEvidence(identity: identity);
        var ambiguous = GovernedLoopSleepContractTestFixture.WakeEvidence(
            GovernedLoopWakeDisposition.AmbiguousAttempt,
            2,
            identity,
            dispositionEvidenceReference: "ambiguous-after-call",
            recordedAtUtc: prepared.RecordedAtUtc.AddSeconds(1));
        var committed = GovernedLoopSleepContractTestFixture.WakeEvidence(
            GovernedLoopWakeDisposition.Committed,
            3,
            identity,
            recordedAtUtc: ambiguous.RecordedAtUtc.AddSeconds(1));
        var differentPrepared = GovernedLoopSleepContractTestFixture.WakeEvidence(
            identity: identity,
            recordedAtUtc: prepared.RecordedAtUtc.AddTicks(1));
        var store = new GovernedLoopSleepStore(paths);
        await store.PublishAndReleaseAsync(checkpoint, postureHash);
        await store.CreateWakeAsync(checkpoint, prepared, postureHash);
        await store.AdvanceWakeAsync(prepared, ambiguous);
        var pending = await new GovernedLoopBackgroundWorkSource(new ScheduleStore(paths), store)
            .ReadAsync(GovernedLoopBackgroundWorkFamily.WakeReconciliation, ambiguous.RecordedAtUtc, 1);
        await store.AdvanceWakeAsync(ambiguous, committed);

        var restarted = new GovernedLoopSleepStore(paths);
        var read = await restarted.ReadWakeAsync(identity.WakeId);
        var terminal = await new GovernedLoopBackgroundWorkSource(new ScheduleStore(paths), restarted)
            .ReadAsync(GovernedLoopBackgroundWorkFamily.WakeReconciliation, committed.RecordedAtUtc, 1);
        var ledger = LatestLedger(paths);
        var beforeReplay = await File.ReadAllBytesAsync(ledger);
        var root = JsonNode.Parse(beforeReplay)!.AsObject();
        var wakeEvidence = (JsonArray)((JsonObject)((JsonArray)root["entries"]!)[0]!)["wakeEvidence"]!;
        var dispositions = wakeEvidence.Select(item => item!["disposition"]!.GetValue<string>()).ToArray();

        var replayInitial = await restarted.CreateWakeAsync(checkpoint, prepared, postureHash);
        var replayAmbiguous = await restarted.AdvanceWakeAsync(prepared, ambiguous);
        var replayTerminal = await restarted.AdvanceWakeAsync(ambiguous, committed);
        var nonExactReplay = await restarted.AdvanceWakeAsync(differentPrepared, ambiguous);

        Assert.Equal(committed, read!.Evidence);
        Assert.Equal(prepared, read.PreparedEvidence);
        Assert.Single(pending!.WakeReconciliationCandidates);
        Assert.Empty(terminal!.WakeReconciliationCandidates);
        Assert.Equal(3, wakeEvidence.Count);
        Assert.Equal(["prepared", "ambiguous-attempt", "committed"], dispositions);
        Assert.Equal(GovernedLoopWakeEvidenceMutationStatus.Replayed, replayInitial!.Status);
        Assert.Equal(committed, replayInitial.Evidence);
        Assert.Equal(GovernedLoopWakeEvidenceMutationStatus.Replayed, replayAmbiguous!.Status);
        Assert.Equal(committed, replayAmbiguous.Evidence);
        Assert.Equal(GovernedLoopWakeEvidenceMutationStatus.Replayed, replayTerminal!.Status);
        Assert.Equal(committed, replayTerminal.Evidence);
        Assert.Equal(GovernedLoopWakeEvidenceMutationStatus.Conflict, nonExactReplay!.Status);
        Assert.Equal(committed, nonExactReplay.Evidence);
        Assert.Equal(ledger, LatestLedger(paths));
        Assert.Equal(beforeReplay, await File.ReadAllBytesAsync(ledger));
    }

    [Fact]
    public async Task Interrupted_wake_advance_is_ambiguous_and_exactly_retryable()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var checkpoint = GovernedLoopSleepContractTestFixture.TimestampCheckpoint();
        var postureHash = GovernedLoopSleepContractTestFixture.Hash('9');
        var identity = GovernedLoopSleepContractTestFixture.WakeIdentity(checkpoint);
        var prepared = GovernedLoopSleepContractTestFixture.WakeEvidence(identity: identity);
        var committed = GovernedLoopSleepContractTestFixture.WakeEvidence(
            GovernedLoopWakeDisposition.Committed,
            2,
            identity,
            recordedAtUtc: prepared.RecordedAtUtc.AddSeconds(1));
        var store = new GovernedLoopSleepStore(paths);
        await store.PublishAndReleaseAsync(checkpoint, postureHash);
        await store.CreateWakeAsync(checkpoint, prepared, postureHash);
        var interrupted = new GovernedLoopSleepStore(paths, new GovernedLoopSleepStoreOptions
        {
            DurableBoundaryObserver = boundary =>
            {
                if (boundary == GovernedLoopSleepStorePersistenceBoundary.PrecursorCreated)
                {
                    throw new IOException("simulated process loss");
                }
            },
        });

        var ambiguous = await interrupted.AdvanceWakeAsync(prepared, committed);
        var retry = await new GovernedLoopSleepStore(paths).AdvanceWakeAsync(prepared, committed);

        Assert.Equal(GovernedLoopWakeEvidenceMutationStatus.Ambiguous, ambiguous!.Status);
        Assert.Equal(GovernedLoopWakeEvidenceMutationStatus.Committed, retry!.Status);
    }

    [Theory]
    [InlineData(GovernedLoopWakeDisposition.Prepared)]
    [InlineData(GovernedLoopWakeDisposition.Committed)]
    [InlineData(GovernedLoopWakeDisposition.Duplicate)]
    [InlineData(GovernedLoopWakeDisposition.Late)]
    [InlineData(GovernedLoopWakeDisposition.Stale)]
    [InlineData(GovernedLoopWakeDisposition.Conflict)]
    [InlineData(GovernedLoopWakeDisposition.Cancelled)]
    [InlineData(GovernedLoopWakeDisposition.Expired)]
    [InlineData(GovernedLoopWakeDisposition.Paused)]
    [InlineData(GovernedLoopWakeDisposition.ReviewBlocked)]
    [InlineData(GovernedLoopWakeDisposition.AmbiguousAttempt)]
    [InlineData(GovernedLoopWakeDisposition.Failed)]
    public async Task Every_closed_wake_disposition_round_trips_canonically(GovernedLoopWakeDisposition disposition)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var checkpoint = GovernedLoopSleepContractTestFixture.TimestampCheckpoint();
        var postureHash = GovernedLoopSleepContractTestFixture.Hash('9');
        var evidence = GovernedLoopSleepContractTestFixture.WakeEvidence(
            disposition,
            identity: GovernedLoopSleepContractTestFixture.WakeIdentity(checkpoint));
        var store = new GovernedLoopSleepStore(paths);
        await store.PublishAndReleaseAsync(checkpoint, postureHash);

        var created = await store.CreateWakeAsync(checkpoint, evidence, postureHash);
        var read = await new GovernedLoopSleepStore(paths).ReadWakeAsync(evidence.Identity.WakeId);

        Assert.Equal(GovernedLoopWakeEvidenceMutationStatus.Committed, created!.Status);
        Assert.Equal(evidence, read!.Evidence);
    }

    [Fact]
    public async Task Concurrent_instances_serialize_checkpoint_claim_and_wake_cas_winners()
    {
        using var publishWorkspace = new TestWorkspace();
        var publishPaths = new WorkspacePaths(publishWorkspace.RootPath);
        var checkpoint = GovernedLoopSleepContractTestFixture.TimestampCheckpoint();
        var publishResults = await Task.WhenAll(
            new GovernedLoopSleepStore(publishPaths).PublishAndReleaseAsync(checkpoint, GovernedLoopSleepContractTestFixture.Hash('8')),
            new GovernedLoopSleepStore(publishPaths).PublishAndReleaseAsync(checkpoint, GovernedLoopSleepContractTestFixture.Hash('9')));
        Assert.Single(publishResults, result => result!.Status == GovernedLoopSleepCheckpointMutationStatus.Committed);
        Assert.Single(publishResults, result => result!.Status == GovernedLoopSleepCheckpointMutationStatus.Conflict);

        using var wakeWorkspace = new TestWorkspace();
        var wakePaths = new WorkspacePaths(wakeWorkspace.RootPath);
        var store = new GovernedLoopSleepStore(wakePaths);
        var postureHash = GovernedLoopSleepContractTestFixture.Hash('9');
        var identity = GovernedLoopSleepContractTestFixture.WakeIdentity(checkpoint);
        var prepared = GovernedLoopSleepContractTestFixture.WakeEvidence(identity: identity);
        var committed = GovernedLoopSleepContractTestFixture.WakeEvidence(
            GovernedLoopWakeDisposition.Committed,
            2,
            identity,
            recordedAtUtc: prepared.RecordedAtUtc.AddSeconds(1));
        var ambiguous = GovernedLoopSleepContractTestFixture.WakeEvidence(
            GovernedLoopWakeDisposition.AmbiguousAttempt,
            2,
            identity,
            dispositionEvidenceReference: "ambiguous-after-call",
            recordedAtUtc: prepared.RecordedAtUtc.AddSeconds(1));
        await store.PublishAndReleaseAsync(checkpoint, postureHash);
        await store.CreateWakeAsync(checkpoint, prepared, postureHash);
        var wakeResults = await Task.WhenAll(
            new GovernedLoopSleepStore(wakePaths).AdvanceWakeAsync(prepared, committed),
            new GovernedLoopSleepStore(wakePaths).AdvanceWakeAsync(prepared, ambiguous));
        Assert.Single(wakeResults, result => result!.Status == GovernedLoopWakeEvidenceMutationStatus.Committed);
        Assert.Single(wakeResults, result => result!.Status == GovernedLoopWakeEvidenceMutationStatus.Conflict);
    }

    [Theory]
    [InlineData(GovernedLoopSleepStorePersistenceBoundary.PrecursorCreated, GovernedLoopSleepCheckpointMutationStatus.Committed)]
    [InlineData(GovernedLoopSleepStorePersistenceBoundary.Staged, GovernedLoopSleepCheckpointMutationStatus.Committed)]
    [InlineData(GovernedLoopSleepStorePersistenceBoundary.Publishing, GovernedLoopSleepCheckpointMutationStatus.Committed)]
    [InlineData(GovernedLoopSleepStorePersistenceBoundary.Published, GovernedLoopSleepCheckpointMutationStatus.Replayed)]
    public async Task Checkpoint_boundary_interruption_is_ambiguous_and_exact_retry_recovers(
        GovernedLoopSleepStorePersistenceBoundary boundary,
        GovernedLoopSleepCheckpointMutationStatus retryStatus)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var checkpoint = GovernedLoopSleepContractTestFixture.TimestampCheckpoint();
        var postureHash = GovernedLoopSleepContractTestFixture.Hash('9');
        var interrupted = new GovernedLoopSleepStore(paths, new GovernedLoopSleepStoreOptions
        {
            DurableBoundaryObserver = observed =>
            {
                if (observed == boundary)
                {
                    throw new IOException("simulated abrupt process loss");
                }
            },
        });

        var first = await interrupted.PublishAndReleaseAsync(checkpoint, postureHash);
        var retry = await new GovernedLoopSleepStore(paths).PublishAndReleaseAsync(checkpoint, postureHash);
        var read = await new GovernedLoopSleepStore(paths).ReadCheckpointAsync(checkpoint.CheckpointId);

        Assert.Equal(GovernedLoopSleepCheckpointMutationStatus.Ambiguous, first!.Status);
        Assert.Equal(retryStatus, retry!.Status);
        Assert.Equal(GovernedLoopSleepStoreReadStatus.Found, read!.Status);
    }

    [Theory]
    [InlineData(GovernedLoopSleepStorePersistenceBoundary.PrecursorCreated, GovernedLoopWakeEvidenceMutationStatus.Committed)]
    [InlineData(GovernedLoopSleepStorePersistenceBoundary.Staged, GovernedLoopWakeEvidenceMutationStatus.Committed)]
    [InlineData(GovernedLoopSleepStorePersistenceBoundary.Publishing, GovernedLoopWakeEvidenceMutationStatus.Committed)]
    [InlineData(GovernedLoopSleepStorePersistenceBoundary.Published, GovernedLoopWakeEvidenceMutationStatus.Replayed)]
    public async Task Wake_boundary_interruption_is_ambiguous_and_exact_retry_recovers(
        GovernedLoopSleepStorePersistenceBoundary boundary,
        GovernedLoopWakeEvidenceMutationStatus retryStatus)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var checkpoint = GovernedLoopSleepContractTestFixture.TimestampCheckpoint();
        var postureHash = GovernedLoopSleepContractTestFixture.Hash('9');
        await new GovernedLoopSleepStore(paths).PublishAndReleaseAsync(checkpoint, postureHash);
        var prepared = GovernedLoopSleepContractTestFixture.WakeEvidence(identity: GovernedLoopSleepContractTestFixture.WakeIdentity(checkpoint));
        var interrupted = new GovernedLoopSleepStore(paths, new GovernedLoopSleepStoreOptions
        {
            DurableBoundaryObserver = observed =>
            {
                if (observed == boundary)
                {
                    throw new IOException("simulated abrupt process loss");
                }
            },
        });

        var first = await interrupted.CreateWakeAsync(checkpoint, prepared, postureHash);
        var retry = await new GovernedLoopSleepStore(paths).CreateWakeAsync(checkpoint, prepared, postureHash);
        var read = await new GovernedLoopSleepStore(paths).ReadWakeAsync(prepared.Identity.WakeId);

        Assert.Equal(GovernedLoopWakeEvidenceMutationStatus.Ambiguous, first!.Status);
        Assert.Equal(retryStatus, retry!.Status);
        Assert.Equal(GovernedLoopSleepStoreReadStatus.Found, read!.Status);
    }

    [Fact]
    public async Task Cancellation_is_honored_before_staging_and_ignored_after_staging_begins()
    {
        using var canceledWorkspace = new TestWorkspace();
        using var preCanceled = new CancellationTokenSource();
        preCanceled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new GovernedLoopSleepStore(new WorkspacePaths(canceledWorkspace.RootPath)).PublishAndReleaseAsync(
                GovernedLoopSleepContractTestFixture.TimestampCheckpoint(),
                GovernedLoopSleepContractTestFixture.Hash('9'),
                preCanceled.Token));

        using var committingWorkspace = new TestWorkspace();
        using var cancellation = new CancellationTokenSource();
        var paths = new WorkspacePaths(committingWorkspace.RootPath);
        var checkpoint = GovernedLoopSleepContractTestFixture.TimestampCheckpoint();
        var store = new GovernedLoopSleepStore(paths, new GovernedLoopSleepStoreOptions
        {
            DurableBoundaryObserver = boundary =>
            {
                if (boundary == GovernedLoopSleepStorePersistenceBoundary.PrecursorCreated)
                {
                    cancellation.Cancel();
                }
            },
        });

        var result = await store.PublishAndReleaseAsync(checkpoint, GovernedLoopSleepContractTestFixture.Hash('9'), cancellation.Token);
        Assert.Equal(GovernedLoopSleepCheckpointMutationStatus.Committed, result!.Status);
    }

    [Fact]
    public async Task Cancellation_while_waiting_for_the_workspace_lease_is_propagated_by_every_operation()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var checkpoint = GovernedLoopSleepContractTestFixture.TimestampCheckpoint();
        var postureHash = GovernedLoopSleepContractTestFixture.Hash('9');
        var waitingStore = new GovernedLoopSleepStore(paths);
        Assert.Equal(GovernedLoopSleepCheckpointMutationStatus.Committed, (await waitingStore.PublishAndReleaseAsync(checkpoint, postureHash))!.Status);
        // https://github.com/Jacob-J-Thomas/agenthome-poc/issues/505
        // Own the real cross-process lock directly so fixture readiness never depends on ThreadPool scheduling.
        using var externalLock = CrossProcessExclusiveFileLock.Acquire(Path.Combine(StoreRoot(paths), ".queue.lock"));
        var secondCheckpoint = GovernedLoopSleepContractTestFixture.TimestampCheckpoint(
            GovernedLoopSleepContractTestFixture.Binding(runId: "cancelled-waiter"));
        var identity = GovernedLoopSleepContractTestFixture.WakeIdentity(checkpoint);
        var prepared = GovernedLoopSleepContractTestFixture.WakeEvidence(identity: identity);
        var committed = GovernedLoopSleepContractTestFixture.WakeEvidence(
            GovernedLoopWakeDisposition.Committed,
            2,
            identity,
            recordedAtUtc: prepared.RecordedAtUtc.AddSeconds(1));
        var background = new GovernedLoopBackgroundWorkSource(new ScheduleStore(paths), waitingStore);

        await AssertCancellationAsync(token => waitingStore.PublishAndReleaseAsync(secondCheckpoint, postureHash, token));
        await AssertCancellationAsync(token => waitingStore.ReadCheckpointAsync(checkpoint.CheckpointId, token));
        await AssertCancellationAsync(token => waitingStore.ReadWakeAsync(identity.WakeId, token));
        await AssertCancellationAsync(token => waitingStore.CreateWakeAsync(
            checkpoint,
            prepared,
            postureHash,
            token));
        await AssertCancellationAsync(token => waitingStore.AdvanceWakeAsync(prepared, committed, token));
        await AssertCancellationAsync(token => background.ReadAsync(
            GovernedLoopBackgroundWorkFamily.Wake,
            checkpoint.PublishedAtUtc,
            1,
            token));

        externalLock.Dispose();
        Assert.Equal(GovernedLoopSleepStoreReadStatus.Found, (await waitingStore.ReadCheckpointAsync(checkpoint.CheckpointId))!.Status);

        static async Task AssertCancellationAsync(Func<CancellationToken, Task> operation)
        {
            using var cancellation = new CancellationTokenSource();
            var pending = operation(cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
    }

    [Fact]
    public async Task Noncanonical_duplicate_unsupported_bom_and_oversize_ledgers_fail_closed()
    {
        await AssertCorruptAsync(bytes => [.. bytes, (byte)' ']);
        await AssertCorruptAsync(bytes => [0xEF, 0xBB, 0xBF, .. bytes]);
        await AssertCorruptAsync(bytes => MutateJson(bytes, root => root["schemaVersion"] = 2));
        await AssertCorruptAsync(bytes => MutateJson(bytes, root => root["entries"] = new JsonObject()));
        await AssertCorruptAsync(bytes => MutateJson(bytes, root => root["entries"] = new JsonArray()));
        await AssertCorruptAsync(bytes => MutateJson(bytes, root =>
            ((JsonObject)((JsonArray)root["entries"]!)[0]!)["publicationPostureHash"] = "not-a-hash"));
        await AssertCorruptAsync(bytes => MutateJson(bytes, root =>
            ((JsonObject)((JsonArray)root["entries"]!)[0]!).Remove("wakeClaimPostureHash")));
        await AssertCorruptAsync(bytes => MutateJson(bytes, root =>
            ((JsonObject)((JsonArray)root["entries"]!)[0]!)["wakeClaimPostureHash"] = GovernedLoopSleepContractTestFixture.Hash('8')));
        await AssertCorruptAsync(bytes => MutateJson(bytes, root =>
        {
            var entry = (JsonObject)((JsonArray)root["entries"]!)[0]!;
            entry.Remove("wakeEvidence");
            entry["wake"] = null;
        }));
        await AssertCorruptAsync(bytes => MutateJson(bytes, root =>
        {
            var entries = (JsonArray)root["entries"]!;
            entries.Add(entries[0]!.DeepClone());
        }));

        using var workspace = new TestWorkspace();
        var limited = await new GovernedLoopSleepStore(
            new WorkspacePaths(workspace.RootPath),
            new GovernedLoopSleepStoreOptions { MaxCatalogUtf8Bytes = 128 }).PublishAndReleaseAsync(
                GovernedLoopSleepContractTestFixture.TimestampCheckpoint(),
                GovernedLoopSleepContractTestFixture.Hash('9'));
        Assert.Equal(GovernedLoopSleepCheckpointMutationStatus.Conflict, limited!.Status);

        using var countWorkspace = new TestWorkspace();
        var countPaths = new WorkspacePaths(countWorkspace.RootPath);
        var countStore = new GovernedLoopSleepStore(countPaths);
        var first = GovernedLoopSleepContractTestFixture.TimestampCheckpoint();
        var second = GovernedLoopSleepContractTestFixture.TimestampCheckpoint(
            GovernedLoopSleepContractTestFixture.Binding(runId: "bounded-read"));
        var postureHash = GovernedLoopSleepContractTestFixture.Hash('9');
        await countStore.PublishAndReleaseAsync(first, postureHash);
        await countStore.PublishAndReleaseAsync(second, postureHash);
        Assert.Equal(
            GovernedLoopSleepStoreReadStatus.Conflict,
            (await new GovernedLoopSleepStore(countPaths, new GovernedLoopSleepStoreOptions { MaxCheckpoints = 1 })
                .ReadCheckpointAsync(first.CheckpointId))!.Status);

        using var generationWorkspace = new TestWorkspace();
        var generationPaths = new WorkspacePaths(generationWorkspace.RootPath);
        await new GovernedLoopSleepStore(generationPaths).PublishAndReleaseAsync(first, postureHash);
        var generationOne = LatestLedger(generationPaths);
        File.Move(generationOne, Path.Combine(StoreRoot(generationPaths), "ledger-0000000000000000002.json"));
        Assert.Equal(
            GovernedLoopSleepStoreReadStatus.Conflict,
            (await new GovernedLoopSleepStore(generationPaths).ReadCheckpointAsync(first.CheckpointId))!.Status);
    }

    [Fact]
    public async Task Checkpoint_publication_reserves_the_exact_maximum_wake_chain_before_releasing_execution()
    {
        var maximumIdentifier = new string('z', GovernedLoopSleepContractLimits.MaxIdentifierCharacters);
        var maximumEvidenceReference = new string('z', GovernedLoopSleepContractLimits.MaxEvidenceReferenceCharacters);
        var checkpoint = GovernedLoopSleepContractTestFixture.EventCheckpoint(maximumEvidenceReference);
        var postureHash = GovernedLoopSleepContractTestFixture.Hash('9');
        var identity = GovernedLoopSleepContractTestFixture.WakeIdentity(checkpoint);
        var prepared = GovernedLoopSleepContractTestFixture.WakeEvidence(
            identity: identity,
            continuationOperationId: maximumIdentifier,
            recordedAtUtc: DateTimeOffset.MaxValue);
        var ambiguous = GovernedLoopSleepContractTestFixture.WakeEvidence(
            GovernedLoopWakeDisposition.AmbiguousAttempt,
            2,
            identity,
            maximumIdentifier,
            dispositionEvidenceReference: maximumEvidenceReference,
            recordedAtUtc: DateTimeOffset.MaxValue);
        var committed = GovernedLoopSleepContractTestFixture.WakeEvidence(
            GovernedLoopWakeDisposition.Committed,
            3,
            identity,
            maximumIdentifier,
            GovernedLoopSleepContractTestFixture.Hash('e'),
            recordedAtUtc: DateTimeOffset.MaxValue);

        using var sizingWorkspace = new TestWorkspace();
        var sizingPaths = new WorkspacePaths(sizingWorkspace.RootPath);
        var sizingStore = new GovernedLoopSleepStore(sizingPaths);
        Assert.Equal(GovernedLoopSleepCheckpointMutationStatus.Committed, (await sizingStore.PublishAndReleaseAsync(checkpoint, postureHash))!.Status);
        Assert.Equal(GovernedLoopWakeEvidenceMutationStatus.Committed, (await sizingStore.CreateWakeAsync(checkpoint, prepared, postureHash))!.Status);
        Assert.Equal(GovernedLoopWakeEvidenceMutationStatus.Committed, (await sizingStore.AdvanceWakeAsync(prepared, ambiguous))!.Status);
        Assert.Equal(GovernedLoopWakeEvidenceMutationStatus.Committed, (await sizingStore.AdvanceWakeAsync(ambiguous, committed))!.Status);
        var generationReservation = long.MaxValue.ToString(CultureInfo.InvariantCulture).Length - 1;
        var exactMaximumBytes = checked((int)new FileInfo(LatestLedger(sizingPaths)).Length + generationReservation);

        using var admittedWorkspace = new TestWorkspace();
        var admittedPaths = new WorkspacePaths(admittedWorkspace.RootPath);
        var admittedStore = new GovernedLoopSleepStore(admittedPaths, new GovernedLoopSleepStoreOptions { MaxCatalogUtf8Bytes = exactMaximumBytes });
        var published = await admittedStore.PublishAndReleaseAsync(checkpoint, postureHash);
        var initial = await admittedStore.CreateWakeAsync(checkpoint, prepared, postureHash);
        var attempted = await admittedStore.AdvanceWakeAsync(prepared, ambiguous);
        var terminal = await admittedStore.AdvanceWakeAsync(ambiguous, committed);
        var restarted = await new GovernedLoopSleepStore(admittedPaths, new GovernedLoopSleepStoreOptions { MaxCatalogUtf8Bytes = exactMaximumBytes })
            .ReadWakeAsync(identity.WakeId);

        Assert.Equal(GovernedLoopSleepCheckpointMutationStatus.Committed, published!.Status);
        Assert.True(new FileInfo(LatestLedger(admittedPaths)).Length < exactMaximumBytes);
        Assert.Equal(GovernedLoopWakeEvidenceMutationStatus.Committed, initial!.Status);
        Assert.Equal(GovernedLoopWakeEvidenceMutationStatus.Committed, attempted!.Status);
        Assert.Equal(GovernedLoopWakeEvidenceMutationStatus.Committed, terminal!.Status);
        Assert.Equal(GovernedLoopSleepStoreReadStatus.Found, restarted!.Status);
        Assert.Equal(committed, restarted.Evidence);

        using var rejectedWorkspace = new TestWorkspace();
        var rejected = await new GovernedLoopSleepStore(
            new WorkspacePaths(rejectedWorkspace.RootPath),
            new GovernedLoopSleepStoreOptions { MaxCatalogUtf8Bytes = exactMaximumBytes - 1 })
            .PublishAndReleaseAsync(checkpoint, postureHash);
        Assert.Equal(GovernedLoopSleepCheckpointMutationStatus.Conflict, rejected!.Status);
    }

    [Theory]
    [InlineData("checkpoint-mode")]
    [InlineData("cycle-iteration")]
    [InlineData("execution-generation")]
    [InlineData("publication-schema")]
    [InlineData("revision-schema")]
    [InlineData("deadline-shape")]
    public async Task Malformed_nested_checkpoint_shapes_fail_closed(string mutation)
    {
        await AssertCorruptAsync(bytes => MutateJson(bytes, root =>
        {
            var checkpoint = (JsonObject)((JsonObject)((JsonArray)root["entries"]!)[0]!)["checkpoint"]!;
            var binding = (JsonObject)checkpoint["binding"]!;
            switch (mutation)
            {
                case "checkpoint-mode":
                    checkpoint["wakeMode"] = "unknown";
                    break;
                case "cycle-iteration":
                    binding["cycleIteration"] = "one";
                    break;
                case "execution-generation":
                    ((JsonObject)binding["execution"]!)["executionGeneration"] = "one";
                    break;
                case "publication-schema":
                    ((JsonObject)binding["publication"]!)["schemaVersion"] = "one";
                    break;
                case "revision-schema":
                    ((JsonObject)((JsonObject)binding["execution"]!)["revision"]!)["schemaVersion"] = "one";
                    break;
                case "deadline-shape":
                    checkpoint["wakeDeadlineUtc"] = 7;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(mutation));
            }
        }));
    }

    [Theory]
    [InlineData("wake-disposition")]
    [InlineData("wake-version")]
    [InlineData("identity-mode")]
    [InlineData("identity-reference")]
    public async Task Malformed_nested_wake_shapes_fail_closed(string mutation)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var checkpoint = GovernedLoopSleepContractTestFixture.EventCheckpoint();
        var postureHash = GovernedLoopSleepContractTestFixture.Hash('9');
        var evidence = GovernedLoopSleepContractTestFixture.WakeEvidence(
            identity: GovernedLoopSleepContractTestFixture.WakeIdentity(checkpoint));
        var store = new GovernedLoopSleepStore(paths);
        await store.PublishAndReleaseAsync(checkpoint, postureHash);
        await store.CreateWakeAsync(checkpoint, evidence, postureHash);
        var ledger = LatestLedger(paths);
        var bytes = await File.ReadAllBytesAsync(ledger);
        await File.WriteAllBytesAsync(ledger, MutateJson(bytes, root =>
        {
            var wakeEvidence = (JsonArray)((JsonObject)((JsonArray)root["entries"]!)[0]!)["wakeEvidence"]!;
            var wake = (JsonObject)wakeEvidence[0]!;
            var identity = (JsonObject)wake["identity"]!;
            switch (mutation)
            {
                case "wake-disposition":
                    wake["disposition"] = "unknown";
                    break;
                case "wake-version":
                    wake["evidenceVersion"] = "one";
                    break;
                case "identity-mode":
                    identity["wakeMode"] = "unknown";
                    break;
                case "identity-reference":
                    identity["authenticatedEventReference"] = 7;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(mutation));
            }
        }));
        Assert.Equal(
            GovernedLoopSleepStoreReadStatus.Conflict,
            (await new GovernedLoopSleepStore(paths).ReadWakeAsync(evidence.Identity.WakeId))!.Status);
    }

    [Theory]
    [InlineData("gap")]
    [InlineData("reorder")]
    [InlineData("tamper")]
    [InlineData("duplicate")]
    [InlineData("over-bound")]
    public async Task Malformed_wake_transition_ledgers_fail_closed(string mutation)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var checkpoint = GovernedLoopSleepContractTestFixture.TimestampCheckpoint();
        var postureHash = GovernedLoopSleepContractTestFixture.Hash('9');
        var identity = GovernedLoopSleepContractTestFixture.WakeIdentity(checkpoint);
        var prepared = GovernedLoopSleepContractTestFixture.WakeEvidence(identity: identity);
        var ambiguous = GovernedLoopSleepContractTestFixture.WakeEvidence(
            GovernedLoopWakeDisposition.AmbiguousAttempt,
            2,
            identity,
            recordedAtUtc: prepared.RecordedAtUtc.AddSeconds(1));
        var committed = GovernedLoopSleepContractTestFixture.WakeEvidence(
            GovernedLoopWakeDisposition.Committed,
            3,
            identity,
            recordedAtUtc: ambiguous.RecordedAtUtc.AddSeconds(1));
        var store = new GovernedLoopSleepStore(paths);
        await store.PublishAndReleaseAsync(checkpoint, postureHash);
        await store.CreateWakeAsync(checkpoint, prepared, postureHash);
        await store.AdvanceWakeAsync(prepared, ambiguous);
        await store.AdvanceWakeAsync(ambiguous, committed);
        var ledger = LatestLedger(paths);
        var bytes = await File.ReadAllBytesAsync(ledger);
        await File.WriteAllBytesAsync(ledger, MutateJson(bytes, root =>
        {
            var wakeEvidence = (JsonArray)((JsonObject)((JsonArray)root["entries"]!)[0]!)["wakeEvidence"]!;
            switch (mutation)
            {
                case "gap":
                    ((JsonObject)wakeEvidence[1]!)["evidenceVersion"] = 3;
                    break;
                case "reorder":
                    var first = wakeEvidence[0]!.DeepClone();
                    var second = wakeEvidence[1]!.DeepClone();
                    wakeEvidence[0] = second;
                    wakeEvidence[1] = first;
                    break;
                case "tamper":
                    ((JsonObject)wakeEvidence[2]!)["contentHash"] = GovernedLoopSleepContractTestFixture.Hash('0');
                    break;
                case "duplicate":
                    wakeEvidence[2] = wakeEvidence[1]!.DeepClone();
                    break;
                case "over-bound":
                    wakeEvidence.Add(wakeEvidence[2]!.DeepClone());
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(mutation));
            }
        }));

        var read = await new GovernedLoopSleepStore(paths).ReadWakeAsync(identity.WakeId);

        Assert.Equal(GovernedLoopSleepStoreReadStatus.Conflict, read!.Status);
        Assert.Null(read.Evidence);
    }

    [Fact]
    public async Task Count_bound_is_workspace_scoped_and_malformed_proposals_never_publish()
    {
        using var first = new TestWorkspace();
        using var second = new TestWorkspace();
        var options = new GovernedLoopSleepStoreOptions { MaxCheckpoints = 1 };
        var firstCheckpoint = GovernedLoopSleepContractTestFixture.TimestampCheckpoint();
        var secondCheckpoint = GovernedLoopSleepContractTestFixture.TimestampCheckpoint(
            GovernedLoopSleepContractTestFixture.Binding(runId: "run-2"));
        var postureHash = GovernedLoopSleepContractTestFixture.Hash('9');
        var firstStore = new GovernedLoopSleepStore(new WorkspacePaths(first.RootPath), options);

        Assert.Equal(GovernedLoopSleepCheckpointMutationStatus.Committed, (await firstStore.PublishAndReleaseAsync(firstCheckpoint, postureHash))!.Status);
        Assert.Equal(GovernedLoopSleepCheckpointMutationStatus.Conflict, (await firstStore.PublishAndReleaseAsync(secondCheckpoint, postureHash))!.Status);
        Assert.Equal(
            GovernedLoopSleepCheckpointMutationStatus.Committed,
            (await new GovernedLoopSleepStore(new WorkspacePaths(second.RootPath), options).PublishAndReleaseAsync(secondCheckpoint, postureHash))!.Status);

        using var malformedWorkspace = new TestWorkspace();
        var malformed = firstCheckpoint with { ContentHash = GovernedLoopSleepContractTestFixture.Hash('0') };
        var malformedResult = await new GovernedLoopSleepStore(new WorkspacePaths(malformedWorkspace.RootPath))
            .PublishAndReleaseAsync(malformed, postureHash);
        Assert.Equal(GovernedLoopSleepCheckpointMutationStatus.Conflict, malformedResult!.Status);
        Assert.False(Directory.Exists(StoreRoot(new WorkspacePaths(malformedWorkspace.RootPath))));
    }

    [Fact]
    public async Task Null_or_missing_mutation_evidence_returns_closed_conflicts_without_publication()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new GovernedLoopSleepStore(paths);
        var checkpoint = GovernedLoopSleepContractTestFixture.TimestampCheckpoint();
        var postureHash = GovernedLoopSleepContractTestFixture.Hash('9');
        var identity = GovernedLoopSleepContractTestFixture.WakeIdentity(checkpoint);
        var prepared = GovernedLoopSleepContractTestFixture.WakeEvidence(identity: identity);
        var committed = GovernedLoopSleepContractTestFixture.WakeEvidence(
            GovernedLoopWakeDisposition.Committed,
            2,
            identity,
            recordedAtUtc: prepared.RecordedAtUtc.AddSeconds(1));

        Assert.Equal(
            GovernedLoopSleepCheckpointMutationStatus.Conflict,
            (await store.PublishAndReleaseAsync(null!, postureHash))!.Status);
        Assert.Equal(
            GovernedLoopWakeEvidenceMutationStatus.Conflict,
            (await store.CreateWakeAsync(null!, prepared, postureHash))!.Status);
        Assert.Equal(
            GovernedLoopWakeEvidenceMutationStatus.Conflict,
            (await store.CreateWakeAsync(checkpoint, null!, postureHash))!.Status);
        Assert.Equal(
            GovernedLoopWakeEvidenceMutationStatus.Conflict,
            (await store.AdvanceWakeAsync(null!, committed))!.Status);
        Assert.Equal(
            GovernedLoopWakeEvidenceMutationStatus.Conflict,
            (await store.AdvanceWakeAsync(prepared, committed))!.Status);
        Assert.Empty(Directory.EnumerateFiles(StoreRoot(paths), "ledger-*.json"));
    }

    [Fact]
    public void Invalid_configuration_is_rejected_before_filesystem_access()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        Assert.Throws<ArgumentOutOfRangeException>(() => new GovernedLoopSleepStore(paths, new GovernedLoopSleepStoreOptions { MaxCheckpoints = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GovernedLoopSleepStore(paths, new GovernedLoopSleepStoreOptions { MaxCatalogUtf8Bytes = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GovernedLoopSleepStore(paths, new GovernedLoopSleepStoreOptions { MaxDurabilityArtifacts = 0 }));
        Assert.False(Directory.Exists(StoreRoot(paths)));
    }

    [Fact]
    public async Task Unix_symlink_fifo_hard_link_and_root_swap_substitutions_fail_closed()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var checkpoint = GovernedLoopSleepContractTestFixture.TimestampCheckpoint();
        var postureHash = GovernedLoopSleepContractTestFixture.Hash('9');
        using var symlinkWorkspace = new TestWorkspace();
        var symlinkPaths = new WorkspacePaths(symlinkWorkspace.RootPath);
        var outside = symlinkWorkspace.File("outside-loops");
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(symlinkPaths.AgentPath);
        File.CreateSymbolicLink(symlinkPaths.AgentFile("loops"), outside);
        var symlinkStore = new GovernedLoopSleepStore(symlinkPaths);
        var identity = GovernedLoopSleepContractTestFixture.WakeIdentity(checkpoint);
        var prepared = GovernedLoopSleepContractTestFixture.WakeEvidence(identity: identity);
        var committed = GovernedLoopSleepContractTestFixture.WakeEvidence(
            GovernedLoopWakeDisposition.Committed,
            2,
            identity,
            recordedAtUtc: prepared.RecordedAtUtc.AddSeconds(1));
        var symlinkResult = await symlinkStore.PublishAndReleaseAsync(checkpoint, postureHash);
        Assert.Equal(GovernedLoopSleepCheckpointMutationStatus.Conflict, symlinkResult!.Status);
        Assert.Equal(
            GovernedLoopWakeEvidenceMutationStatus.Conflict,
            (await symlinkStore.CreateWakeAsync(checkpoint, prepared, postureHash))!.Status);
        Assert.Equal(
            GovernedLoopWakeEvidenceMutationStatus.Conflict,
            (await symlinkStore.AdvanceWakeAsync(prepared, committed))!.Status);
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));

        using var fifoWorkspace = new TestWorkspace();
        var fifoPaths = new WorkspacePaths(fifoWorkspace.RootPath);
        Directory.CreateDirectory(StoreRoot(fifoPaths));
        Assert.Equal(0, MkFifo(Path.Combine(StoreRoot(fifoPaths), "ledger-0000000000000000001.json"), Convert.ToUInt32("600", 8)));
        var fifoResult = await new GovernedLoopSleepStore(fifoPaths).PublishAndReleaseAsync(checkpoint, postureHash);
        Assert.Equal(GovernedLoopSleepCheckpointMutationStatus.Conflict, fifoResult!.Status);

        using var hardLinkWorkspace = new TestWorkspace();
        var hardLinkPaths = new WorkspacePaths(hardLinkWorkspace.RootPath);
        await new GovernedLoopSleepStore(hardLinkPaths).PublishAndReleaseAsync(checkpoint, postureHash);
        var ledger = LatestLedger(hardLinkPaths);
        Assert.Equal(0, Link(ledger, hardLinkWorkspace.File("linked-ledger")));
        var hardLinkResult = await new GovernedLoopSleepStore(hardLinkPaths).ReadCheckpointAsync(checkpoint.CheckpointId);
        Assert.Equal(GovernedLoopSleepStoreReadStatus.Conflict, hardLinkResult!.Status);

        using var swapWorkspace = new TestWorkspace();
        var swapPaths = new WorkspacePaths(swapWorkspace.RootPath);
        var swapRoot = StoreRoot(swapPaths);
        var movedRoot = swapRoot + "-moved";
        var swapping = new GovernedLoopSleepStore(swapPaths, new GovernedLoopSleepStoreOptions
        {
            DurableBoundaryObserver = boundary =>
            {
                if (boundary == GovernedLoopSleepStorePersistenceBoundary.Publishing)
                {
                    Directory.Move(swapRoot, movedRoot);
                    Directory.CreateDirectory(swapRoot);
                    File.WriteAllText(Path.Combine(swapRoot, "sentinel"), "untouched");
                }
            },
        });
        var swapResult = await swapping.PublishAndReleaseAsync(checkpoint, postureHash);
        Assert.Equal(GovernedLoopSleepCheckpointMutationStatus.Conflict, swapResult!.Status);
        Assert.Equal("untouched", await File.ReadAllTextAsync(Path.Combine(swapRoot, "sentinel")));
        Assert.Empty(Directory.EnumerateFiles(movedRoot, "ledger-*.json"));
    }

    [Fact]
    public async Task Two_process_exact_publication_race_has_one_commit_and_one_replay()
    {
        using var workspace = new TestWorkspace();
        var gate = workspace.File("release-sleep-hosts");
        var firstReady = workspace.File("first-sleep-ready");
        var secondReady = workspace.File("second-sleep-ready");
        var firstOutput = workspace.File("first-sleep-output");
        var secondOutput = workspace.File("second-sleep-output");
        using var first = StartCrossProcessHost(workspace.RootPath, gate, firstReady, firstOutput);
        using var second = StartCrossProcessHost(workspace.RootPath, gate, secondReady, secondOutput);
        await Task.WhenAll(WaitForPathAsync(firstReady), WaitForPathAsync(secondReady));
        await File.WriteAllTextAsync(gate, "go");
        await Task.WhenAll(first.WaitForExitAsync(), second.WaitForExitAsync()).WaitAsync(TimeSpan.FromSeconds(30));
        await AssertProcessSucceededAsync(first);
        await AssertProcessSucceededAsync(second);
        var results = new[] { await File.ReadAllTextAsync(firstOutput), await File.ReadAllTextAsync(secondOutput) };
        Assert.Single(results, status => status == GovernedLoopSleepCheckpointMutationStatus.Committed.ToString());
        Assert.Single(results, status => status == GovernedLoopSleepCheckpointMutationStatus.Replayed.ToString());
    }

    [Fact]
    public async Task External_process_loss_after_staging_recovers_without_partial_publication()
    {
        using var workspace = new TestWorkspace();
        var gate = workspace.File("release-crash-host");
        var ready = workspace.File("crash-host-ready");
        var output = workspace.File("crash-host-output");
        using var process = StartCrossProcessHost(
            workspace.RootPath,
            gate,
            ready,
            output,
            GovernedLoopSleepStorePersistenceBoundary.Staged);
        await WaitForPathAsync(ready);
        await File.WriteAllTextAsync(gate, "go");
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotEqual(0, process.ExitCode);

        var paths = new WorkspacePaths(workspace.RootPath);
        var checkpoint = GovernedLoopSleepContractTestFixture.TimestampCheckpoint();
        var recovered = await new GovernedLoopSleepStore(paths).PublishAndReleaseAsync(checkpoint, GovernedLoopSleepContractTestFixture.Hash('9'));
        Assert.Equal(GovernedLoopSleepCheckpointMutationStatus.Committed, recovered!.Status);
        Assert.DoesNotContain(Directory.EnumerateFiles(StoreRoot(paths)), path =>
            Path.GetFileName(path).StartsWith(".staged-", StringComparison.Ordinal)
            || Path.GetFileName(path).StartsWith(".discard-", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("checkpoint", GovernedLoopSleepStorePersistenceBoundary.PrecursorCreated, GovernedLoopSleepCheckpointMutationStatus.Committed)]
    [InlineData("checkpoint", GovernedLoopSleepStorePersistenceBoundary.Staged, GovernedLoopSleepCheckpointMutationStatus.Committed)]
    [InlineData("checkpoint", GovernedLoopSleepStorePersistenceBoundary.Publishing, GovernedLoopSleepCheckpointMutationStatus.Committed)]
    [InlineData("checkpoint", GovernedLoopSleepStorePersistenceBoundary.Published, GovernedLoopSleepCheckpointMutationStatus.Replayed)]
    public async Task External_process_loss_at_every_checkpoint_boundary_has_one_recoverable_decision(
        string operation,
        GovernedLoopSleepStorePersistenceBoundary boundary,
        GovernedLoopSleepCheckpointMutationStatus retryStatus)
    {
        using var workspace = new TestWorkspace();
        var gate = workspace.File($"release-{boundary}-checkpoint-host");
        var ready = workspace.File($"{boundary}-checkpoint-ready");
        var output = workspace.File($"{boundary}-checkpoint-output");
        using var process = StartCrossProcessHost(workspace.RootPath, gate, ready, output, boundary, operation);
        await WaitForPathAsync(ready);
        await File.WriteAllTextAsync(gate, "go");
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotEqual(0, process.ExitCode);
        var retry = await new GovernedLoopSleepStore(new WorkspacePaths(workspace.RootPath)).PublishAndReleaseAsync(
            GovernedLoopSleepContractTestFixture.TimestampCheckpoint(),
            GovernedLoopSleepContractTestFixture.Hash('9'));
        Assert.Equal(retryStatus, retry!.Status);
    }

    [Theory]
    [InlineData(GovernedLoopSleepStorePersistenceBoundary.PrecursorCreated, GovernedLoopWakeEvidenceMutationStatus.Committed)]
    [InlineData(GovernedLoopSleepStorePersistenceBoundary.Staged, GovernedLoopWakeEvidenceMutationStatus.Committed)]
    [InlineData(GovernedLoopSleepStorePersistenceBoundary.Publishing, GovernedLoopWakeEvidenceMutationStatus.Committed)]
    [InlineData(GovernedLoopSleepStorePersistenceBoundary.Published, GovernedLoopWakeEvidenceMutationStatus.Replayed)]
    public async Task External_process_loss_at_every_wake_boundary_has_one_recoverable_decision(
        GovernedLoopSleepStorePersistenceBoundary boundary,
        GovernedLoopWakeEvidenceMutationStatus retryStatus)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var checkpoint = GovernedLoopSleepContractTestFixture.TimestampCheckpoint();
        var postureHash = GovernedLoopSleepContractTestFixture.Hash('9');
        await new GovernedLoopSleepStore(paths).PublishAndReleaseAsync(checkpoint, postureHash);
        var gate = workspace.File($"release-{boundary}-wake-host");
        var ready = workspace.File($"{boundary}-wake-ready");
        var output = workspace.File($"{boundary}-wake-output");
        using var process = StartCrossProcessHost(workspace.RootPath, gate, ready, output, boundary, "wake");
        await WaitForPathAsync(ready);
        await File.WriteAllTextAsync(gate, "go");
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotEqual(0, process.ExitCode);
        var prepared = GovernedLoopSleepContractTestFixture.WakeEvidence(
            identity: GovernedLoopSleepContractTestFixture.WakeIdentity(checkpoint));
        var retry = await new GovernedLoopSleepStore(paths).CreateWakeAsync(checkpoint, prepared, postureHash);
        Assert.Equal(retryStatus, retry!.Status);
    }

    [Theory]
    [InlineData(GovernedLoopSleepStorePersistenceBoundary.PrecursorCreated, GovernedLoopWakeEvidenceMutationStatus.Committed)]
    [InlineData(GovernedLoopSleepStorePersistenceBoundary.Staged, GovernedLoopWakeEvidenceMutationStatus.Committed)]
    [InlineData(GovernedLoopSleepStorePersistenceBoundary.Publishing, GovernedLoopWakeEvidenceMutationStatus.Committed)]
    [InlineData(GovernedLoopSleepStorePersistenceBoundary.Published, GovernedLoopWakeEvidenceMutationStatus.Replayed)]
    public async Task External_process_loss_at_every_terminal_evidence_boundary_has_one_recoverable_decision(
        GovernedLoopSleepStorePersistenceBoundary boundary,
        GovernedLoopWakeEvidenceMutationStatus retryStatus)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var checkpoint = GovernedLoopSleepContractTestFixture.TimestampCheckpoint();
        var postureHash = GovernedLoopSleepContractTestFixture.Hash('9');
        var prepared = GovernedLoopSleepContractTestFixture.WakeEvidence(
            identity: GovernedLoopSleepContractTestFixture.WakeIdentity(checkpoint));
        var committed = GovernedLoopSleepContractTestFixture.WakeEvidence(
            GovernedLoopWakeDisposition.Committed,
            evidenceVersion: 2,
            identity: prepared.Identity,
            continuationEvidenceHash: GovernedLoopSleepContractTestFixture.Hash('8'));
        var store = new GovernedLoopSleepStore(paths);
        await store.PublishAndReleaseAsync(checkpoint, postureHash);
        await store.CreateWakeAsync(checkpoint, prepared, postureHash);
        var gate = workspace.File($"release-{boundary}-terminal-evidence-host");
        var ready = workspace.File($"{boundary}-terminal-evidence-ready");
        var output = workspace.File($"{boundary}-terminal-evidence-output");
        using var process = StartCrossProcessHost(workspace.RootPath, gate, ready, output, boundary, "advance");
        await WaitForPathAsync(ready);
        await File.WriteAllTextAsync(gate, "go");
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotEqual(0, process.ExitCode);

        var retry = await new GovernedLoopSleepStore(paths).AdvanceWakeAsync(prepared, committed);

        Assert.Equal(retryStatus, retry!.Status);
        Assert.Equal(GovernedLoopWakeDisposition.Committed, retry.Evidence!.Disposition);
        Assert.Equal(committed.ContentHash, retry.Evidence.ContentHash);
    }

    [Fact]
    public async Task Cross_process_sleep_store_host()
    {
        var workspace = Environment.GetEnvironmentVariable(CrossProcessWorkspace);
        if (string.IsNullOrEmpty(workspace))
        {
            return;
        }

        var ready = Environment.GetEnvironmentVariable(CrossProcessReady)!;
        var gate = Environment.GetEnvironmentVariable(CrossProcessGate)!;
        var output = Environment.GetEnvironmentVariable(CrossProcessOutput)!;
        await File.WriteAllTextAsync(ready, "ready");
        await WaitForPathAsync(gate);
        Action<GovernedLoopSleepStorePersistenceBoundary>? observer = null;
        if (Enum.TryParse<GovernedLoopSleepStorePersistenceBoundary>(
            Environment.GetEnvironmentVariable(CrossProcessCrashBoundary),
            out var crashBoundary))
        {
            observer = boundary =>
            {
                if (boundary == crashBoundary)
                {
                    TerminateCrossProcessHost();
                }
            };
        }

        var store = new GovernedLoopSleepStore(
            new WorkspacePaths(workspace),
            new GovernedLoopSleepStoreOptions { DurableBoundaryObserver = observer });
        var checkpoint = GovernedLoopSleepContractTestFixture.TimestampCheckpoint();
        var postureHash = GovernedLoopSleepContractTestFixture.Hash('9');
        string status;
        var operation = Environment.GetEnvironmentVariable(CrossProcessOperation);
        if (string.Equals(operation, "wake", StringComparison.Ordinal))
        {
            var prepared = GovernedLoopSleepContractTestFixture.WakeEvidence(
                identity: GovernedLoopSleepContractTestFixture.WakeIdentity(checkpoint));
            status = (await store.CreateWakeAsync(checkpoint, prepared, postureHash))!.Status.ToString();
        }
        else if (string.Equals(operation, "advance", StringComparison.Ordinal))
        {
            var prepared = GovernedLoopSleepContractTestFixture.WakeEvidence(
                identity: GovernedLoopSleepContractTestFixture.WakeIdentity(checkpoint));
            var committed = GovernedLoopSleepContractTestFixture.WakeEvidence(
                GovernedLoopWakeDisposition.Committed,
                evidenceVersion: 2,
                identity: prepared.Identity,
                continuationEvidenceHash: GovernedLoopSleepContractTestFixture.Hash('8'));
            status = (await store.AdvanceWakeAsync(prepared, committed))!.Status.ToString();
        }
        else
        {
            status = (await store.PublishAndReleaseAsync(checkpoint, postureHash))!.Status.ToString();
        }

        await File.WriteAllTextAsync(output, status);
    }

    private static async Task AssertCorruptAsync(Func<byte[], byte[]> mutation)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var checkpoint = GovernedLoopSleepContractTestFixture.TimestampCheckpoint();
        await new GovernedLoopSleepStore(paths).PublishAndReleaseAsync(checkpoint, GovernedLoopSleepContractTestFixture.Hash('9'));
        var ledger = LatestLedger(paths);
        await File.WriteAllBytesAsync(ledger, mutation(await File.ReadAllBytesAsync(ledger)));
        Assert.Equal(GovernedLoopSleepStoreReadStatus.Conflict, (await new GovernedLoopSleepStore(paths).ReadCheckpointAsync(checkpoint.CheckpointId))!.Status);
    }

    private static byte[] MutateJson(byte[] bytes, Action<JsonObject> mutation)
    {
        var root = JsonNode.Parse(bytes)!.AsObject();
        mutation(root);
        return Encoding.UTF8.GetBytes(root.ToJsonString());
    }

    private static string StoreRoot(WorkspacePaths paths)
        => paths.AgentFile(Path.Combine("loops", "execution", "sleep"));

    private static string LatestLedger(WorkspacePaths paths)
        => Directory.EnumerateFiles(StoreRoot(paths), "ledger-*.json").Order(StringComparer.Ordinal).Last();

    private static Process StartCrossProcessHost(
        string workspace,
        string gate,
        string ready,
        string output,
        GovernedLoopSleepStorePersistenceBoundary? crashBoundary = null,
        string operation = "checkpoint")
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetTempPath(),
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        const string CrossProcessHostTestName = "EmbodySense.Core.Persistence.Tests.Loops.Execution.Sleep.GovernedLoopSleepStoreTests.Cross_process_sleep_store_host";
        if (crashBoundary is not null)
        {
            Verification.CoverageChildProcessAssembly.AddExpectedTerminationVstestArguments(startInfo, typeof(GovernedLoopSleepStoreTests).Assembly.Location, CrossProcessHostTestName);
        }
        else
        {
            Verification.CoverageChildProcessAssembly.AddVstestArguments(startInfo, typeof(GovernedLoopSleepStoreTests).Assembly.Location, CrossProcessHostTestName);
        }
        startInfo.Environment["DOTNET_ROLL_FORWARD"] = "Major";
        startInfo.Environment[CrossProcessWorkspace] = workspace;
        startInfo.Environment[CrossProcessGate] = gate;
        startInfo.Environment[CrossProcessReady] = ready;
        startInfo.Environment[CrossProcessOutput] = output;
        startInfo.Environment[CrossProcessOperation] = operation;
        if (crashBoundary is not null)
        {
            startInfo.Environment[CrossProcessCrashBoundary] = crashBoundary.Value.ToString();
        }

        return Process.Start(startInfo) ?? throw new InvalidOperationException("Cross-process sleep-store test host did not start.");
    }

    private static async Task WaitForPathAsync(string path)
    {
        var wait = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            Assert.True(wait.Elapsed < TimeSpan.FromSeconds(60), $"Cross-process sleep host did not create `{path}`.");
            await Task.Delay(10);
        }
    }

    private static async Task AssertProcessSucceededAsync(Process process)
    {
        var standardError = await process.StandardError.ReadToEndAsync();
        var standardOutput = await process.StandardOutput.ReadToEndAsync();
        Assert.True(process.ExitCode == 0, standardError + Environment.NewLine + standardOutput);
    }

    private static void TerminateCrossProcessHost()
    {
        Process.GetCurrentProcess().Kill();
        Thread.Sleep(Timeout.Infinite);
    }

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MkFifo(string path, uint mode);

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link(string existingPath, string newPath);
}
