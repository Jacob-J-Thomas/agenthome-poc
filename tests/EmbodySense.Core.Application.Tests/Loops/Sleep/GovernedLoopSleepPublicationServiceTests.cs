using EmbodySense.Core.Application.Loops.Sleep;
using EmbodySense.Core.Application.Loops.Sleep.Models;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Application.Tests.Loops.Sleep;

public sealed class GovernedLoopSleepPublicationServiceTests
{
    [Fact]
    public void Constructor_requires_all_ports()
    {
        var store = new InMemoryGovernedLoopSleepStore();
        var posture = new StubGovernedLoopSleepCurrentPosturePort();
        var continuation = new StubGovernedLoopWakeContinuationPort();
        var authentication = new StubGovernedLoopAuthenticatedWakeVerificationPort();

        Assert.Throws<ArgumentNullException>(() => new GovernedLoopSleepService(null!, posture, continuation, authentication));
        Assert.Throws<ArgumentNullException>(() => new GovernedLoopSleepService(store, null!, continuation, authentication));
        Assert.Throws<ArgumentNullException>(() => new GovernedLoopSleepService(store, posture, null!, authentication));
        Assert.Throws<ArgumentNullException>(() => new GovernedLoopSleepService(store, posture, continuation, null!));
    }

    [Fact]
    public async Task Publish_commits_exact_checkpoint_before_owner_release_and_replays_identity()
    {
        var harness = new GovernedLoopSleepApplicationHarness();

        var first = await harness.Service.PublishAsync(harness.PublicationRequest);
        var replay = await harness.Service.PublishAsync(harness.PublicationRequest);

        Assert.Equal(GovernedLoopSleepPublicationStatus.Published, first.Status);
        Assert.Equal(GovernedLoopSleepPublicationStatus.Replayed, replay.Status);
        Assert.Equal(first.Checkpoint!.CheckpointId, replay.Checkpoint!.CheckpointId);
        Assert.Equal(first.Checkpoint.ContentHash, replay.Checkpoint.ContentHash);
        Assert.Equal(harness.Posture.Execution.Frontier.Payload.ContentHash, first.Checkpoint.Binding.FrontierHash);
        Assert.Equal(harness.Posture.Execution.Frontier.Payload.FrontierVersion, first.Checkpoint.Binding.FrontierVersion);
        Assert.Equal(1, harness.Store.CheckpointCount);
        Assert.Equal(2, harness.CurrentPosture.ReadCount);
    }

    [Fact]
    public async Task Publish_replay_rejects_a_checkpoint_recorded_after_the_current_trusted_time()
    {
        var harness = new GovernedLoopSleepApplicationHarness();
        var first = await harness.Service.PublishAsync(harness.PublicationRequest);
        var rolledBackAtUtc = GovernedLoopSleepApplicationTestFixture.Now.AddSeconds(-30);
        harness.TimeProvider.UtcNow = rolledBackAtUtc;
        harness.CurrentPosture.Result = new GovernedLoopSleepCurrentPostureReadResult(
            GovernedLoopSleepCurrentPostureReadStatus.Found,
            GovernedLoopSleepApplicationTestFixture.Posture(observedAtUtc: rolledBackAtUtc));

        var replay = await harness.Service.PublishAsync(harness.PublicationRequest);

        Assert.Equal(GovernedLoopSleepPublicationStatus.Invalid, replay.Status);
        Assert.NotNull(first.Checkpoint);
        Assert.Equal(GovernedLoopSleepApplicationTestFixture.Now, first.Checkpoint.PublishedAtUtc);
        Assert.Equal(1, harness.Store.CheckpointCount);
    }

    [Fact]
    public async Task Publish_reconciles_crash_after_checkpoint_commit_by_deterministic_identity()
    {
        var harness = new GovernedLoopSleepApplicationHarness();
        harness.Store.ThrowAfterPublishCommit = true;

        var result = await harness.Service.PublishAsync(harness.PublicationRequest);

        Assert.Equal(GovernedLoopSleepPublicationStatus.Replayed, result.Status);
        Assert.NotNull(result.Checkpoint);
        Assert.Equal(1, harness.Store.CheckpointCount);
    }

    [Fact]
    public async Task Publish_fails_closed_when_posture_or_clock_is_unavailable()
    {
        var postureFailure = new GovernedLoopSleepApplicationHarness();
        postureFailure.CurrentPosture.Exception = new InvalidOperationException("posture unavailable");

        var clockFailure = new GovernedLoopSleepApplicationHarness();
        clockFailure.TimeProvider.ThrowOnCall = 2;

        Assert.Equal(
            GovernedLoopSleepPublicationStatus.Unavailable,
            (await postureFailure.Service.PublishAsync(postureFailure.PublicationRequest)).Status);
        Assert.Equal(
            GovernedLoopSleepPublicationStatus.Unavailable,
            (await clockFailure.Service.PublishAsync(clockFailure.PublicationRequest)).Status);
        Assert.Equal(0, postureFailure.Store.CheckpointCount);
        Assert.Equal(0, clockFailure.Store.CheckpointCount);
    }

    [Fact]
    public async Task Publish_preserves_ambiguity_when_store_fails_before_commit_or_reconciliation_read()
    {
        var precommitFailure = new GovernedLoopSleepApplicationHarness();
        precommitFailure.Store.ThrowBeforePublish = true;

        var readFailure = new GovernedLoopSleepApplicationHarness();
        readFailure.Store.ThrowBeforePublish = true;
        readFailure.Store.CheckpointReadException = new InvalidOperationException("checkpoint read unavailable");

        Assert.Equal(
            GovernedLoopSleepPublicationStatus.Ambiguous,
            (await precommitFailure.Service.PublishAsync(precommitFailure.PublicationRequest)).Status);
        Assert.Equal(
            GovernedLoopSleepPublicationStatus.Ambiguous,
            (await readFailure.Service.PublishAsync(readFailure.PublicationRequest)).Status);
        Assert.Equal(0, precommitFailure.Store.CheckpointCount);
        Assert.Equal(0, readFailure.Store.CheckpointCount);
    }

    [Fact]
    public async Task Publish_accepts_exact_waiting_activation_under_running_active_sibling_frontier()
    {
        var waiting = GovernedLoopSleepApplicationTestFixture.WaitingNode();
        var posture = GovernedLoopSleepApplicationTestFixture.Posture(
            node: waiting,
            lifecycleStatus: GovernedLoopRunStatus.Running,
            frontierStatus: GovernedLoopFrontierStatus.Active,
            nodes: [waiting, GovernedLoopSleepApplicationTestFixture.ReadyNode()]);
        var harness = new GovernedLoopSleepApplicationHarness(posture);

        var published = await harness.Service.PublishAsync(harness.PublicationRequest);
        var wake = await harness.Service.WakeAsync(new GovernedLoopWakeRequest(
            published.Checkpoint!.CheckpointId,
            published.Checkpoint.ContentHash));

        Assert.Equal(GovernedLoopSleepPublicationStatus.Published, published.Status);
        Assert.Equal(GovernedLoopWakeResultStatus.Committed, wake.Status);
        Assert.Equal("wait-node", wake.Evidence!.Identity.CheckpointId == published.Checkpoint.CheckpointId
            ? published.Checkpoint.Binding.NodeId
            : null);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task Publish_rejects_stale_generation_frontier_activation_cycle_visit_and_attempt(int substitution)
    {
        var harness = new GovernedLoopSleepApplicationHarness();
        var binding = harness.PublicationRequest.Binding;
        var changed = substitution switch
        {
            0 => new GovernedLoopSleepBinding(
                GovernedLoopSleepApplicationTestFixture.Binding(2),
                binding.Publication,
                binding.FrontierVersion,
                binding.FrontierHash,
                binding.ActivationOrdinal,
                binding.CycleId,
                binding.CycleIteration,
                binding.NodeId,
                binding.NodeVisitOrdinal,
                binding.WaitAttempt,
                binding.WaitOperationId),
            1 => Copy(binding, frontierVersion: binding.FrontierVersion + 1),
            2 => Copy(binding, activationOrdinal: binding.ActivationOrdinal + 1),
            3 => Copy(binding, cycleId: "cycle-2", cycleIteration: 2),
            4 => Copy(binding, nodeVisitOrdinal: binding.NodeVisitOrdinal + 1),
            _ => Copy(binding, waitAttempt: binding.WaitAttempt + 1, waitOperationId: "wait-operation-2")
        };
        var request = harness.PublicationRequest with { Binding = changed };

        var result = await harness.Service.PublishAsync(request);

        Assert.Equal(GovernedLoopSleepPublicationStatus.Stale, result.Status);
        Assert.Equal(0, harness.Store.CheckpointCount);
    }

    [Theory]
    [InlineData(GovernedLoopRunStatus.CancelRequested, true, GovernedLoopSleepPublicationStatus.Cancelled)]
    [InlineData(GovernedLoopRunStatus.Paused, true, GovernedLoopSleepPublicationStatus.Paused)]
    [InlineData(GovernedLoopRunStatus.Waiting, false, GovernedLoopSleepPublicationStatus.ReviewBlocked)]
    public async Task Publish_blocks_non_unattended_lifecycle_posture(
        GovernedLoopRunStatus lifecycle,
        bool unattended,
        GovernedLoopSleepPublicationStatus expected)
    {
        var posture = GovernedLoopSleepApplicationTestFixture.Posture(lifecycleStatus: lifecycle, unattended: unattended);
        var harness = new GovernedLoopSleepApplicationHarness(posture);

        var result = await harness.Service.PublishAsync(harness.PublicationRequest);

        Assert.Equal(expected, result.Status);
        Assert.Equal(0, harness.Store.CheckpointCount);
    }

    [Fact]
    public async Task Publish_blocks_expired_and_open_effect_posture()
    {
        var expired = GovernedLoopSleepApplicationTestFixture.Posture(expiresAtUtc: GovernedLoopSleepApplicationTestFixture.Now);
        var expiredHarness = new GovernedLoopSleepApplicationHarness(expired);
        var binding = GovernedLoopSleepApplicationTestFixture.Binding();
        var open = GovernedLoopSleepApplicationTestFixture.Posture(
            binding,
            effects: [GovernedLoopSleepApplicationTestFixture.OpenEffect(binding)]);
        var openHarness = new GovernedLoopSleepApplicationHarness(open);

        Assert.Equal(
            GovernedLoopSleepPublicationStatus.Expired,
            (await expiredHarness.Service.PublishAsync(expiredHarness.PublicationRequest)).Status);
        Assert.Equal(
            GovernedLoopSleepPublicationStatus.AmbiguousAttempt,
            (await openHarness.Service.PublishAsync(openHarness.PublicationRequest)).Status);
    }

    [Fact]
    public async Task Publish_fails_closed_for_null_future_or_inconsistent_posture_outputs()
    {
        var harness = new GovernedLoopSleepApplicationHarness();
        harness.CurrentPosture.Result = null;
        Assert.Equal(
            GovernedLoopSleepPublicationStatus.Invalid,
            (await harness.Service.PublishAsync(harness.PublicationRequest)).Status);

        harness.CurrentPosture.Result = new GovernedLoopSleepCurrentPostureReadResult(
            GovernedLoopSleepCurrentPostureReadStatus.Found,
            harness.Posture with { ObservedAtUtc = GovernedLoopSleepApplicationTestFixture.Now.AddSeconds(1) });
        Assert.Equal(
            GovernedLoopSleepPublicationStatus.Invalid,
            (await harness.Service.PublishAsync(harness.PublicationRequest)).Status);

        harness.CurrentPosture.Result = new GovernedLoopSleepCurrentPostureReadResult(
            GovernedLoopSleepCurrentPostureReadStatus.NotFound,
            harness.Posture);
        Assert.Equal(
            GovernedLoopSleepPublicationStatus.Invalid,
            (await harness.Service.PublishAsync(harness.PublicationRequest)).Status);
    }

    [Theory]
    [InlineData(GovernedLoopSleepCurrentPostureReadStatus.NotFound, GovernedLoopSleepPublicationStatus.NotFound)]
    [InlineData(GovernedLoopSleepCurrentPostureReadStatus.Conflict, GovernedLoopSleepPublicationStatus.Conflict)]
    [InlineData(GovernedLoopSleepCurrentPostureReadStatus.Unavailable, GovernedLoopSleepPublicationStatus.Unavailable)]
    public async Task Publish_maps_conclusive_posture_read_failures(
        GovernedLoopSleepCurrentPostureReadStatus readStatus,
        GovernedLoopSleepPublicationStatus expected)
    {
        var harness = new GovernedLoopSleepApplicationHarness();
        harness.CurrentPosture.Result = new GovernedLoopSleepCurrentPostureReadResult(readStatus);

        var result = await harness.Service.PublishAsync(harness.PublicationRequest);

        Assert.Equal(expected, result.Status);
    }

    [Theory]
    [InlineData(GovernedLoopSleepCheckpointMutationStatus.Conflict, GovernedLoopSleepPublicationStatus.Conflict)]
    [InlineData(GovernedLoopSleepCheckpointMutationStatus.Unavailable, GovernedLoopSleepPublicationStatus.Unavailable)]
    [InlineData(GovernedLoopSleepCheckpointMutationStatus.Ambiguous, GovernedLoopSleepPublicationStatus.Ambiguous)]
    public async Task Publish_maps_store_failures_without_releasing_unproved_work(
        GovernedLoopSleepCheckpointMutationStatus storeStatus,
        GovernedLoopSleepPublicationStatus expected)
    {
        var harness = new GovernedLoopSleepApplicationHarness();
        harness.Store.PublishOverride = new GovernedLoopSleepCheckpointMutationResult(storeStatus);

        var result = await harness.Service.PublishAsync(harness.PublicationRequest);

        Assert.Equal(expected, result.Status);
        Assert.Null(result.Checkpoint);
    }

    [Theory]
    [InlineData(GovernedLoopSleepStoreReadStatus.Conflict, GovernedLoopSleepPublicationStatus.Conflict)]
    [InlineData(GovernedLoopSleepStoreReadStatus.Unavailable, GovernedLoopSleepPublicationStatus.Ambiguous)]
    public async Task Publish_reconciles_ambiguous_write_with_authoritative_read_status(
        GovernedLoopSleepStoreReadStatus readStatus,
        GovernedLoopSleepPublicationStatus expected)
    {
        var harness = new GovernedLoopSleepApplicationHarness();
        harness.Store.PublishOverride = new GovernedLoopSleepCheckpointMutationResult(
            GovernedLoopSleepCheckpointMutationStatus.Ambiguous);
        harness.Store.CheckpointReadOverride = new GovernedLoopSleepCheckpointReadResult(readStatus);

        var result = await harness.Service.PublishAsync(harness.PublicationRequest);

        Assert.Equal(expected, result.Status);
        Assert.Null(result.Checkpoint);
    }

    [Fact]
    public async Task Publish_validates_request_clock_and_cancellation_before_mutation()
    {
        var harness = new GovernedLoopSleepApplicationHarness();
        Assert.Equal(GovernedLoopSleepPublicationStatus.Invalid, (await harness.Service.PublishAsync(null)).Status);
        harness.TimeProvider.UtcNow = GovernedLoopSleepApplicationTestFixture.Now.ToOffset(TimeSpan.FromHours(1));
        Assert.Equal(
            GovernedLoopSleepPublicationStatus.Invalid,
            (await harness.Service.PublishAsync(harness.PublicationRequest)).Status);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harness.Service.PublishAsync(harness.PublicationRequest, cancellation.Token));
        Assert.Equal(0, harness.Store.CheckpointCount);
    }

    [Fact]
    public async Task Publish_rejects_null_store_mutation_output()
    {
        var harness = new GovernedLoopSleepApplicationHarness();
        harness.Store.ReturnNullPublish = true;

        var result = await harness.Service.PublishAsync(harness.PublicationRequest);

        Assert.Equal(GovernedLoopSleepPublicationStatus.Invalid, result.Status);
        Assert.Null(result.Checkpoint);
    }

    [Fact]
    public async Task Publish_rejects_malformed_request_and_store_mutation_shapes()
    {
        var nullBinding = new GovernedLoopSleepApplicationHarness();
        var request = new GovernedLoopSleepPublicationRequest(
            null!,
            GovernedLoopWakeMode.Timestamp,
            GovernedLoopSleepApplicationTestFixture.Now,
            null);
        Assert.Equal(GovernedLoopSleepPublicationStatus.Invalid, (await nullBinding.Service.PublishAsync(request)).Status);

        var missingArtifact = new GovernedLoopSleepApplicationHarness();
        missingArtifact.Store.PublishOverride = new GovernedLoopSleepCheckpointMutationResult(
            GovernedLoopSleepCheckpointMutationStatus.Committed);
        Assert.Equal(
            GovernedLoopSleepPublicationStatus.Invalid,
            (await missingArtifact.Service.PublishAsync(missingArtifact.PublicationRequest)).Status);

        var unknownStatus = new GovernedLoopSleepApplicationHarness();
        unknownStatus.Store.PublishOverride = new GovernedLoopSleepCheckpointMutationResult(
            (GovernedLoopSleepCheckpointMutationStatus)int.MaxValue);
        Assert.Equal(
            GovernedLoopSleepPublicationStatus.Invalid,
            (await unknownStatus.Service.PublishAsync(unknownStatus.PublicationRequest)).Status);
    }

    private static GovernedLoopSleepBinding Copy(
        GovernedLoopSleepBinding source,
        long? frontierVersion = null,
        int? activationOrdinal = null,
        string? cycleId = null,
        int? cycleIteration = null,
        int? nodeVisitOrdinal = null,
        int? waitAttempt = null,
        string? waitOperationId = null)
        => new(
            source.Execution,
            source.Publication,
            frontierVersion ?? source.FrontierVersion,
            source.FrontierHash,
            activationOrdinal ?? source.ActivationOrdinal,
            cycleId ?? source.CycleId,
            cycleIteration ?? source.CycleIteration,
            source.NodeId,
            nodeVisitOrdinal ?? source.NodeVisitOrdinal,
            waitAttempt ?? source.WaitAttempt,
            waitOperationId ?? source.WaitOperationId);
}
