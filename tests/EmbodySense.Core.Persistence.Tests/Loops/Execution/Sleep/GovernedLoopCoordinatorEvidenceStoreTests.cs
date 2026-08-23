using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using EmbodySense.Core.Application.Loops.Sleep.Models;
using EmbodySense.Core.Common.Loops.Execution.Sleep;
using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;
using EmbodySense.Core.Common.Tests.Loops.Execution.Sleep;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops.Execution.Sleep;
using EmbodySense.Core.Persistence.Loops.Execution.Sleep.Models;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Persistence.Tests.Loops.Execution.Sleep;

public sealed class GovernedLoopCoordinatorEvidenceStoreTests
{
    private const string CrossProcessWorkspace = "EMBODYSENSE_COORDINATOR_STORE_WORKSPACE";
    private const string CrossProcessGate = "EMBODYSENSE_COORDINATOR_STORE_GATE";
    private const string CrossProcessReady = "EMBODYSENSE_COORDINATOR_STORE_READY";
    private const string CrossProcessOutput = "EMBODYSENSE_COORDINATOR_STORE_OUTPUT";
    private const string CrossProcessMode = "EMBODYSENSE_COORDINATOR_STORE_MODE";
    private const string CrossProcessOwnerId = "EMBODYSENSE_COORDINATOR_STORE_OWNER";
    private const string CrossProcessEpoch = "EMBODYSENSE_COORDINATOR_STORE_EPOCH";
    private const string CrossProcessAcquiredAt = "EMBODYSENSE_COORDINATOR_STORE_ACQUIRED";
    private const string CrossProcessExpectedOwnership = "EMBODYSENSE_COORDINATOR_STORE_EXPECTED_OWNERSHIP";
    private const string CrossProcessExpectedHeartbeat = "EMBODYSENSE_COORDINATOR_STORE_EXPECTED_HEARTBEAT";

    [Fact]
    public async Task Atomic_acquisition_read_restart_and_exact_retry_return_detached_evidence()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var request = Acquisition();

        var acquired = await new GovernedLoopCoordinatorEvidenceStore(paths).TryAcquireAsync(request);
        var read = await new GovernedLoopCoordinatorEvidenceStore(paths).ReadAsync(request.ProposedOwnership.CoordinatorId);
        var duplicate = await new GovernedLoopCoordinatorEvidenceStore(paths).TryAcquireAsync(request);

        Assert.Equal(GovernedLoopCoordinatorAcquisitionStatus.Acquired, acquired!.Status);
        Assert.Equal(GovernedLoopCoordinatorReadStatus.Found, read!.Status);
        Assert.Equal(request.ProposedOwnership, read.Snapshot!.Ownership);
        Assert.Equal(request.StartingLifecycle, read.Snapshot.LatestLifecycle);
        Assert.Equal(request.InitialHeartbeat, read.Snapshot.LatestHeartbeat);
        Assert.Equal(0, read.Snapshot.LatestFailureSequence);
        Assert.Null(read.Snapshot.LatestFailureHash);
        Assert.NotSame(request.ProposedOwnership, read.Snapshot.Ownership);
        Assert.NotSame(request.StartingLifecycle, read.Snapshot.LatestLifecycle);
        Assert.Equal(GovernedLoopCoordinatorAcquisitionStatus.Duplicate, duplicate!.Status);
        Assert.Equal(read.Snapshot, duplicate.Snapshot);
    }

    [Fact]
    public async Task Heartbeat_lifecycle_and_failure_heads_are_contiguous_replayable_and_conflict_aware()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new GovernedLoopCoordinatorEvidenceStore(paths);
        var acquisition = Acquisition();
        await store.TryAcquireAsync(acquisition);
        var owner = acquisition.ProposedOwnership;
        var heartbeat2 = GovernedLoopSleepContractTestFixture.Heartbeat(
            2,
            owner,
            acquisition.InitialHeartbeat.RecordedAtUtc.AddSeconds(30),
            acquisition.InitialHeartbeat.LeaseExpiresAtUtc.AddMinutes(1));
        var heartbeatRequest = new GovernedLoopCoordinatorHeartbeatMutationRequest(
            owner,
            owner.ContentHash,
            1,
            acquisition.InitialHeartbeat.ContentHash,
            heartbeat2);
        var running = GovernedLoopSleepContractTestFixture.Lifecycle(
            GovernedLoopCoordinatorStatus.Running,
            2,
            owner,
            acquisition.StartingLifecycle.UpdatedAtUtc.AddSeconds(1));
        var lifecycleRequest = new GovernedLoopCoordinatorLifecycleMutationRequest(
            owner,
            owner.ContentHash,
            1,
            acquisition.StartingLifecycle.ContentHash,
            running);
        var failure = GovernedLoopSleepContractTestFixture.Failure(
            ownership: owner,
            occurredAtUtc: running.UpdatedAtUtc.AddSeconds(1));
        var failureRequest = new GovernedLoopCoordinatorFailureMutationRequest(
            owner,
            owner.ContentHash,
            GovernedLoopCoordinatorPriorFailureExpectation.None,
            0,
            null,
            failure);

        var heartbeat = await store.RenewHeartbeatAsync(heartbeatRequest);
        var heartbeatDuplicate = await new GovernedLoopCoordinatorEvidenceStore(paths).RenewHeartbeatAsync(heartbeatRequest);
        var lifecycle = await store.AppendLifecycleAsync(lifecycleRequest);
        var lifecycleDuplicate = await new GovernedLoopCoordinatorEvidenceStore(paths).AppendLifecycleAsync(lifecycleRequest);
        var appendedFailure = await store.AppendFailureAsync(failureRequest);
        var failureDuplicate = await new GovernedLoopCoordinatorEvidenceStore(paths).AppendFailureAsync(failureRequest);
        var read = await new GovernedLoopCoordinatorEvidenceStore(paths).ReadAsync(owner.CoordinatorId);

        Assert.Equal(GovernedLoopCoordinatorHeartbeatMutationStatus.Renewed, heartbeat!.Status);
        Assert.Equal(GovernedLoopCoordinatorHeartbeatMutationStatus.Duplicate, heartbeatDuplicate!.Status);
        Assert.Equal(GovernedLoopCoordinatorLifecycleMutationStatus.Appended, lifecycle!.Status);
        Assert.Equal(GovernedLoopCoordinatorLifecycleMutationStatus.Duplicate, lifecycleDuplicate!.Status);
        Assert.Equal(GovernedLoopCoordinatorFailureMutationStatus.Appended, appendedFailure!.Status);
        Assert.Equal(GovernedLoopCoordinatorFailureMutationStatus.Duplicate, failureDuplicate!.Status);
        Assert.Equal(heartbeat2, read!.Snapshot!.LatestHeartbeat);
        Assert.Equal(running, read.Snapshot.LatestLifecycle);
        Assert.Equal(1, read.Snapshot.LatestFailureSequence);
        Assert.Equal(failure.ContentHash, read.Snapshot.LatestFailureHash);

        var conflictingHeartbeat = new GovernedLoopCoordinatorHeartbeatMutationRequest(
            owner,
            owner.ContentHash,
            1,
            GovernedLoopSleepContractTestFixture.Hash('f'),
            GovernedLoopSleepContractTestFixture.Heartbeat(
                2,
                owner,
                heartbeat2.RecordedAtUtc,
                heartbeat2.LeaseExpiresAtUtc.AddSeconds(1)));
        Assert.Equal(
            GovernedLoopCoordinatorHeartbeatMutationStatus.Conflict,
            (await store.RenewHeartbeatAsync(conflictingHeartbeat))!.Status);
    }

    [Fact]
    public async Task Heartbeat_history_rotates_canonically_without_exhausting_renewal_or_handoff_capacity()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var options = new GovernedLoopCoordinatorEvidenceStoreOptions
        {
            MaxEvidenceItemsPerCoordinator = 10,
            MaxDurabilityArtifacts = 1
        };
        var store = new GovernedLoopCoordinatorEvidenceStore(paths, options);
        var acquisition = Acquisition();
        Assert.Equal(GovernedLoopCoordinatorAcquisitionStatus.Acquired, (await store.TryAcquireAsync(acquisition))!.Status);
        var running = GovernedLoopSleepContractTestFixture.Lifecycle(
            GovernedLoopCoordinatorStatus.Running,
            2,
            acquisition.ProposedOwnership,
            acquisition.StartingLifecycle.UpdatedAtUtc.AddSeconds(1));
        Assert.Equal(
            GovernedLoopCoordinatorLifecycleMutationStatus.Appended,
            (await store.AppendLifecycleAsync(new(
                acquisition.ProposedOwnership,
                acquisition.ProposedOwnership.ContentHash,
                acquisition.StartingLifecycle.LifecycleVersion,
                acquisition.StartingLifecycle.ContentHash,
                running)))!.Status);
        var latest = acquisition.InitialHeartbeat;
        for (var sequence = 2; sequence <= 65; sequence++)
        {
            var next = GovernedLoopSleepContractTestFixture.Heartbeat(
                sequence,
                acquisition.ProposedOwnership,
                latest.RecordedAtUtc.AddSeconds(1),
                latest.LeaseExpiresAtUtc.AddSeconds(1));
            var renewed = await store.RenewHeartbeatAsync(new(
                acquisition.ProposedOwnership,
                acquisition.ProposedOwnership.ContentHash,
                latest.HeartbeatSequence,
                latest.ContentHash,
                next));
            Assert.True(
                renewed!.Status == GovernedLoopCoordinatorHeartbeatMutationStatus.Renewed,
                $"Heartbeat sequence {sequence} returned {renewed.Status} instead of Renewed.");
            latest = next;
        }

        var stopping = GovernedLoopSleepContractTestFixture.Lifecycle(
            GovernedLoopCoordinatorStatus.Stopping,
            3,
            acquisition.ProposedOwnership,
            latest.RecordedAtUtc.AddSeconds(1));
        Assert.Equal(
            GovernedLoopCoordinatorLifecycleMutationStatus.Appended,
            (await store.AppendLifecycleAsync(new(
                acquisition.ProposedOwnership,
                acquisition.ProposedOwnership.ContentHash,
                running.LifecycleVersion,
                running.ContentHash,
                stopping)))!.Status);
        var stopped = GovernedLoopSleepContractTestFixture.Lifecycle(
            GovernedLoopCoordinatorStatus.Stopped,
            4,
            acquisition.ProposedOwnership,
            stopping.UpdatedAtUtc.AddSeconds(1));
        Assert.Equal(
            GovernedLoopCoordinatorLifecycleMutationStatus.Appended,
            (await store.AppendLifecycleAsync(new(
                acquisition.ProposedOwnership,
                acquisition.ProposedOwnership.ContentHash,
                stopping.LifecycleVersion,
                stopping.ContentHash,
                stopped)))!.Status);

        var restarted = new GovernedLoopCoordinatorEvidenceStore(paths, options);
        var read = await restarted.ReadAsync(acquisition.ProposedOwnership.CoordinatorId);
        var duplicate = await restarted.TryAcquireAsync(acquisition);
        var successorOwnership = GovernedLoopSleepContractTestFixture.Ownership(
            ownerId: "process-owner-2",
            ownershipEpoch: 2,
            acquiredAtUtc: latest.LeaseExpiresAtUtc);
        var successor = Acquisition(
            successorOwnership,
            GovernedLoopCoordinatorPriorEvidenceExpectation.Existing,
            acquisition.ProposedOwnership.ContentHash,
            latest.ContentHash);
        var handoff = await restarted.TryAcquireAsync(successor);
        var successorRunning = GovernedLoopSleepContractTestFixture.Lifecycle(
            GovernedLoopCoordinatorStatus.Running,
            2,
            successorOwnership,
            successor.StartingLifecycle.UpdatedAtUtc.AddSeconds(1));
        var successorStarted = await restarted.AppendLifecycleAsync(new(
            successorOwnership,
            successorOwnership.ContentHash,
            successor.StartingLifecycle.LifecycleVersion,
            successor.StartingLifecycle.ContentHash,
            successorRunning));
        var afterHandoff = await new GovernedLoopCoordinatorEvidenceStore(paths, options)
            .ReadAsync(acquisition.ProposedOwnership.CoordinatorId);

        Assert.Equal(GovernedLoopCoordinatorReadStatus.Found, read!.Status);
        Assert.Equal(latest, read.Snapshot!.LatestHeartbeat);
        Assert.Equal(GovernedLoopCoordinatorAcquisitionStatus.Duplicate, duplicate!.Status);
        Assert.Equal(GovernedLoopCoordinatorAcquisitionStatus.Acquired, handoff!.Status);
        Assert.Equal(GovernedLoopCoordinatorLifecycleMutationStatus.Appended, successorStarted!.Status);
        Assert.Equal(successorOwnership, afterHandoff!.Snapshot!.Ownership);
        Assert.Equal(successor.InitialHeartbeat, afterHandoff.Snapshot.LatestHeartbeat);
        Assert.Equal(successorRunning, afterHandoff.Snapshot.LatestLifecycle);

        var root = JsonNode.Parse(await File.ReadAllBytesAsync(LatestLedger(paths)))!.AsObject();
        var entry = (JsonObject)((JsonArray)root["entries"]!)[0]!;
        var retirement = Assert.IsType<JsonObject>(Assert.Single(Assert.IsType<JsonArray>(entry["heartbeatRetirements"])));
        Assert.Equal(65, retirement["retiredCount"]!.GetValue<long>());
        Assert.Equal(latest.ContentHash, retirement["retiredThroughHeartbeatHash"]!.GetValue<string>());
        Assert.Equal(acquisition.InitialHeartbeat.ContentHash, retirement["initialHeartbeatHash"]!.GetValue<string>());
        Assert.Single(Assert.IsType<JsonArray>(entry["heartbeats"]));

        retirement["chainHash"] = GovernedLoopSleepContractTestFixture.Hash('0');
        await File.WriteAllBytesAsync(LatestLedger(paths), Encoding.UTF8.GetBytes(root.ToJsonString()));
        Assert.Equal(
            GovernedLoopCoordinatorReadStatus.Corrupt,
            (await new GovernedLoopCoordinatorEvidenceStore(paths, options).ReadAsync(acquisition.ProposedOwnership.CoordinatorId))!.Status);
    }

    [Fact]
    public async Task Aggregate_catalog_byte_pressure_retires_heartbeat_history_before_append()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var initialOptions = new GovernedLoopCoordinatorEvidenceStoreOptions
        {
            MaxEvidenceItemsPerCoordinator = 64,
        };
        var store = new GovernedLoopCoordinatorEvidenceStore(paths, initialOptions);
        var acquisition = Acquisition();
        Assert.Equal(GovernedLoopCoordinatorAcquisitionStatus.Acquired, (await store.TryAcquireAsync(acquisition))!.Status);
        var latest = acquisition.InitialHeartbeat;
        for (var sequence = 2; sequence <= 10; sequence++)
        {
            var next = GovernedLoopSleepContractTestFixture.Heartbeat(
                sequence,
                acquisition.ProposedOwnership,
                latest.RecordedAtUtc.AddSeconds(1),
                latest.LeaseExpiresAtUtc.AddSeconds(1));
            Assert.Equal(
                GovernedLoopCoordinatorHeartbeatMutationStatus.Renewed,
                (await store.RenewHeartbeatAsync(new(
                    acquisition.ProposedOwnership,
                    acquisition.ProposedOwnership.ContentHash,
                    latest.HeartbeatSequence,
                    latest.ContentHash,
                    next)))!.Status);
            latest = next;
        }

        var exactCurrentBytes = checked((int)new FileInfo(LatestLedger(paths)).Length);
        var boundedOptions = new GovernedLoopCoordinatorEvidenceStoreOptions
        {
            MaxEvidenceItemsPerCoordinator = initialOptions.MaxEvidenceItemsPerCoordinator,
            MaxCatalogUtf8Bytes = exactCurrentBytes,
        };
        var bounded = new GovernedLoopCoordinatorEvidenceStore(paths, boundedOptions);
        var appended = GovernedLoopSleepContractTestFixture.Heartbeat(
            11,
            acquisition.ProposedOwnership,
            latest.RecordedAtUtc.AddSeconds(1),
            latest.LeaseExpiresAtUtc.AddSeconds(1));
        var result = await bounded.RenewHeartbeatAsync(new(
            acquisition.ProposedOwnership,
            acquisition.ProposedOwnership.ContentHash,
            latest.HeartbeatSequence,
            latest.ContentHash,
            appended));

        Assert.Equal(GovernedLoopCoordinatorHeartbeatMutationStatus.Renewed, result!.Status);
        Assert.Equal(appended, result.Snapshot!.LatestHeartbeat);
        Assert.True(new FileInfo(LatestLedger(paths)).Length <= exactCurrentBytes);
        Assert.Equal(
            appended,
            (await new GovernedLoopCoordinatorEvidenceStore(paths, boundedOptions)
                .ReadAsync(acquisition.ProposedOwnership.CoordinatorId))!.Snapshot!.LatestHeartbeat);
    }

    [Theory]
    [InlineData(GovernedLoopCoordinatorStatus.Running)]
    [InlineData(GovernedLoopCoordinatorStatus.Stopping)]
    [InlineData(GovernedLoopCoordinatorStatus.Stopped)]
    [InlineData(GovernedLoopCoordinatorStatus.Failed)]
    public async Task Every_lifecycle_posture_round_trips_canonically(GovernedLoopCoordinatorStatus status)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new GovernedLoopCoordinatorEvidenceStore(paths);
        var acquisition = Acquisition();
        await store.TryAcquireAsync(acquisition);
        var proposed = GovernedLoopSleepContractTestFixture.Lifecycle(
            status,
            2,
            acquisition.ProposedOwnership,
            acquisition.StartingLifecycle.UpdatedAtUtc.AddSeconds(1));

        var appended = await store.AppendLifecycleAsync(new(
            acquisition.ProposedOwnership,
            acquisition.ProposedOwnership.ContentHash,
            1,
            acquisition.StartingLifecycle.ContentHash,
            proposed));
        var read = await new GovernedLoopCoordinatorEvidenceStore(paths).ReadAsync(
            acquisition.ProposedOwnership.CoordinatorId);

        Assert.Equal(GovernedLoopCoordinatorLifecycleMutationStatus.Appended, appended!.Status);
        Assert.Equal(proposed, read!.Snapshot!.LatestLifecycle);
    }

    [Theory]
    [InlineData(GovernedLoopCoordinatorFailureKind.OwnershipLost)]
    [InlineData(GovernedLoopCoordinatorFailureKind.HeartbeatExpired)]
    [InlineData(GovernedLoopCoordinatorFailureKind.StoreUnavailable)]
    [InlineData(GovernedLoopCoordinatorFailureKind.CorruptState)]
    [InlineData(GovernedLoopCoordinatorFailureKind.Backpressured)]
    [InlineData(GovernedLoopCoordinatorFailureKind.ShutdownInterrupted)]
    [InlineData(GovernedLoopCoordinatorFailureKind.Unexpected)]
    public async Task Every_failure_kind_round_trips_canonically(GovernedLoopCoordinatorFailureKind kind)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new GovernedLoopCoordinatorEvidenceStore(paths);
        var acquisition = Acquisition();
        await store.TryAcquireAsync(acquisition);
        var failure = GovernedLoopSleepContractTestFixture.Failure(
            ownership: acquisition.ProposedOwnership,
            kind: kind,
            detailEvidenceReference: kind == GovernedLoopCoordinatorFailureKind.Unexpected ? null : "coordinator-failure-1",
            occurredAtUtc: acquisition.StartingLifecycle.UpdatedAtUtc.AddSeconds(1));

        var appended = await store.AppendFailureAsync(new(
            acquisition.ProposedOwnership,
            acquisition.ProposedOwnership.ContentHash,
            GovernedLoopCoordinatorPriorFailureExpectation.None,
            0,
            null,
            failure));
        var read = await new GovernedLoopCoordinatorEvidenceStore(paths).ReadAsync(
            acquisition.ProposedOwnership.CoordinatorId);

        Assert.Equal(GovernedLoopCoordinatorFailureMutationStatus.Appended, appended!.Status);
        Assert.Equal(failure.ContentHash, read!.Snapshot!.LatestFailureHash);
    }

    [Fact]
    public async Task Handoff_requires_exact_expired_lease_and_stale_or_aba_owner_cannot_mutate_successor()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new GovernedLoopCoordinatorEvidenceStore(paths);
        var first = Acquisition();
        await store.TryAcquireAsync(first);
        var prematureOwner = GovernedLoopSleepContractTestFixture.Ownership(
            ownerId: "process-owner-2",
            ownershipEpoch: 2,
            acquiredAtUtc: first.InitialHeartbeat.LeaseExpiresAtUtc.AddTicks(-1));
        var premature = Acquisition(
            prematureOwner,
            GovernedLoopCoordinatorPriorEvidenceExpectation.Existing,
            first.ProposedOwnership.ContentHash,
            first.InitialHeartbeat.ContentHash);
        Assert.Equal(GovernedLoopCoordinatorAcquisitionStatus.LeaseNotExpired, (await store.TryAcquireAsync(premature))!.Status);

        var successorOwner = GovernedLoopSleepContractTestFixture.Ownership(
            ownerId: "process-owner-2",
            ownershipEpoch: 2,
            acquiredAtUtc: first.InitialHeartbeat.LeaseExpiresAtUtc);
        var successor = Acquisition(
            successorOwner,
            GovernedLoopCoordinatorPriorEvidenceExpectation.Existing,
            first.ProposedOwnership.ContentHash,
            first.InitialHeartbeat.ContentHash);
        var handoff = await new GovernedLoopCoordinatorEvidenceStore(paths).TryAcquireAsync(successor);
        Assert.Equal(GovernedLoopCoordinatorAcquisitionStatus.Acquired, handoff!.Status);
        Assert.Equal(2, handoff.Snapshot!.Ownership.OwnershipEpoch);
        Assert.Equal("process-owner-2", handoff.Snapshot.Ownership.OwnerId);
        Assert.NotEqual(first.ProposedOwnership.ContentHash, handoff.Snapshot.Ownership.ContentHash);

        var abaOwner = GovernedLoopSleepContractTestFixture.Ownership(
            ownerId: first.ProposedOwnership.OwnerId,
            ownershipEpoch: 3,
            acquiredAtUtc: successor.InitialHeartbeat.LeaseExpiresAtUtc);
        var aba = Acquisition(
            abaOwner,
            GovernedLoopCoordinatorPriorEvidenceExpectation.Existing,
            successor.ProposedOwnership.ContentHash,
            successor.InitialHeartbeat.ContentHash);
        var abaHandoff = await store.TryAcquireAsync(aba);
        Assert.Equal(GovernedLoopCoordinatorAcquisitionStatus.Acquired, abaHandoff!.Status);
        Assert.Equal(3, abaHandoff.Snapshot!.Ownership.OwnershipEpoch);
        Assert.Equal(first.ProposedOwnership.OwnerId, abaHandoff.Snapshot.Ownership.OwnerId);

        var staleHeartbeat = GovernedLoopSleepContractTestFixture.Heartbeat(
            2,
            first.ProposedOwnership,
            first.InitialHeartbeat.RecordedAtUtc.AddSeconds(1),
            first.InitialHeartbeat.LeaseExpiresAtUtc.AddMinutes(1));
        var stale = await store.RenewHeartbeatAsync(new(
            first.ProposedOwnership,
            first.ProposedOwnership.ContentHash,
            1,
            first.InitialHeartbeat.ContentHash,
            staleHeartbeat));
        Assert.Equal(GovernedLoopCoordinatorHeartbeatMutationStatus.OwnershipLost, stale!.Status);
        Assert.Equal(abaOwner.ContentHash, stale.Snapshot!.Ownership.ContentHash);

        var replay = await store.TryAcquireAsync(successor);
        Assert.Equal(GovernedLoopCoordinatorAcquisitionStatus.Duplicate, replay!.Status);
        var staleExpected = successor with { ExpectedHeartbeatHash = GovernedLoopSleepContractTestFixture.Hash('0') };
        var conflict = await store.TryAcquireAsync(staleExpected);
        Assert.Equal(GovernedLoopCoordinatorAcquisitionStatus.Duplicate, conflict!.Status);
    }

    [Fact]
    public async Task Concurrent_acquisition_and_cas_have_one_durable_winner()
    {
        using var acquisitionWorkspace = new TestWorkspace();
        var paths = new WorkspacePaths(acquisitionWorkspace.RootPath);
        var first = Acquisition();
        var secondOwner = GovernedLoopSleepContractTestFixture.Ownership(ownerId: "process-owner-2");
        var second = Acquisition(secondOwner);
        var acquisitions = await Task.WhenAll(
            new GovernedLoopCoordinatorEvidenceStore(paths).TryAcquireAsync(first),
            new GovernedLoopCoordinatorEvidenceStore(paths).TryAcquireAsync(second));
        Assert.Single(acquisitions, result => result!.Status == GovernedLoopCoordinatorAcquisitionStatus.Acquired);
        Assert.Single(acquisitions, result => result!.Status == GovernedLoopCoordinatorAcquisitionStatus.OwnedByLivePeer);

        var current = (await new GovernedLoopCoordinatorEvidenceStore(paths).ReadAsync("background-coordinator"))!.Snapshot!;
        var heartbeatA = GovernedLoopSleepContractTestFixture.Heartbeat(
            2,
            current.Ownership,
            current.LatestHeartbeat.RecordedAtUtc.AddSeconds(1),
            current.LatestHeartbeat.LeaseExpiresAtUtc.AddMinutes(1));
        var heartbeatB = GovernedLoopSleepContractTestFixture.Heartbeat(
            2,
            current.Ownership,
            current.LatestHeartbeat.RecordedAtUtc.AddSeconds(2),
            current.LatestHeartbeat.LeaseExpiresAtUtc.AddMinutes(2));
        var results = await Task.WhenAll(
            new GovernedLoopCoordinatorEvidenceStore(paths).RenewHeartbeatAsync(new(
                current.Ownership,
                current.Ownership.ContentHash,
                1,
                current.LatestHeartbeat.ContentHash,
                heartbeatA)),
            new GovernedLoopCoordinatorEvidenceStore(paths).RenewHeartbeatAsync(new(
                current.Ownership,
                current.Ownership.ContentHash,
                1,
                current.LatestHeartbeat.ContentHash,
                heartbeatB)));
        Assert.Single(results, result => result!.Status == GovernedLoopCoordinatorHeartbeatMutationStatus.Renewed);
        Assert.Single(results, result => result!.Status == GovernedLoopCoordinatorHeartbeatMutationStatus.Conflict);
    }

    [Fact]
    public async Task Two_process_acquisition_handoff_and_aba_fencing_have_one_authoritative_owner()
    {
        using var workspace = new TestWorkspace();
        var initial = await RunCrossProcessRaceAsync(workspace.RootPath, "initial", "process-owner-1", "process-owner-2");
        Assert.Single(initial, status => status == GovernedLoopCoordinatorAcquisitionStatus.Acquired.ToString());
        Assert.Single(initial, status => status == GovernedLoopCoordinatorAcquisitionStatus.OwnedByLivePeer.ToString());

        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new GovernedLoopCoordinatorEvidenceStore(paths);
        var first = (await store.ReadAsync("background-coordinator"))!.Snapshot!;
        var handoff = await RunCrossProcessRaceAsync(
            workspace.RootPath,
            "handoff",
            "process-owner-3",
            "process-owner-4",
            2,
            first.LatestHeartbeat.LeaseExpiresAtUtc,
            first.Ownership.ContentHash,
            first.LatestHeartbeat.ContentHash);
        Assert.Single(handoff, status => status == GovernedLoopCoordinatorAcquisitionStatus.Acquired.ToString());
        Assert.Single(handoff, status => status == GovernedLoopCoordinatorAcquisitionStatus.Conflict.ToString());

        var second = (await store.ReadAsync("background-coordinator"))!.Snapshot!;
        var abaOwner = GovernedLoopSleepContractTestFixture.Ownership(
            ownerId: first.Ownership.OwnerId,
            ownershipEpoch: 3,
            acquiredAtUtc: second.LatestHeartbeat.LeaseExpiresAtUtc);
        var aba = Acquisition(
            abaOwner,
            GovernedLoopCoordinatorPriorEvidenceExpectation.Existing,
            second.Ownership.ContentHash,
            second.LatestHeartbeat.ContentHash);
        Assert.Equal(GovernedLoopCoordinatorAcquisitionStatus.Acquired, (await store.TryAcquireAsync(aba))!.Status);

        var staleGate = workspace.File("release-stale-owner");
        var staleReady = workspace.File("stale-owner-ready");
        var staleOutput = workspace.File("stale-owner-output");
        using var stale = StartCrossProcessHost(
            workspace.RootPath,
            staleGate,
            staleReady,
            staleOutput,
            "stale",
            first.Ownership.OwnerId,
            first.Ownership.OwnershipEpoch,
            first.Ownership.AcquiredAtUtc,
            first.Ownership.ContentHash,
            first.LatestHeartbeat.ContentHash);
        await WaitForPathAsync(staleReady);
        await File.WriteAllTextAsync(staleGate, "go");
        await stale.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        await AssertProcessSucceededAsync(stale);
        Assert.Equal(GovernedLoopCoordinatorHeartbeatMutationStatus.OwnershipLost.ToString(), await File.ReadAllTextAsync(staleOutput));
        var current = (await store.ReadAsync("background-coordinator"))!.Snapshot!;
        Assert.Equal(3, current.Ownership.OwnershipEpoch);
        Assert.Equal(first.Ownership.OwnerId, current.Ownership.OwnerId);
        Assert.NotEqual(first.Ownership.ContentHash, current.Ownership.ContentHash);
    }

    [Fact]
    public async Task Cross_process_coordinator_store_host()
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
        var mode = Environment.GetEnvironmentVariable(CrossProcessMode)!;
        var ownerId = Environment.GetEnvironmentVariable(CrossProcessOwnerId)!;
        var store = new GovernedLoopCoordinatorEvidenceStore(new WorkspacePaths(workspace));
        string status;
        if (string.Equals(mode, "stale", StringComparison.Ordinal))
        {
            var ownership = GovernedLoopSleepContractTestFixture.Ownership(
                ownerId: ownerId,
                ownershipEpoch: ParseEpoch(),
                acquiredAtUtc: ParseAcquiredAt());
            var heartbeat = GovernedLoopSleepContractTestFixture.Heartbeat(
                ownership: ownership,
                recordedAtUtc: ownership.AcquiredAtUtc,
                leaseExpiresAtUtc: ownership.AcquiredAtUtc.AddMinutes(1));
            var proposed = GovernedLoopSleepContractTestFixture.Heartbeat(
                2,
                ownership,
                heartbeat.RecordedAtUtc.AddSeconds(1),
                heartbeat.LeaseExpiresAtUtc.AddMinutes(1));
            var result = await store.RenewHeartbeatAsync(new(
                ownership,
                Environment.GetEnvironmentVariable(CrossProcessExpectedOwnership)!,
                1,
                Environment.GetEnvironmentVariable(CrossProcessExpectedHeartbeat)!,
                proposed));
            status = result!.Status.ToString();
        }
        else
        {
            var ownership = GovernedLoopSleepContractTestFixture.Ownership(
                ownerId: ownerId,
                ownershipEpoch: ParseEpoch(),
                acquiredAtUtc: ParseAcquiredAt());
            var expectation = string.Equals(mode, "initial", StringComparison.Ordinal)
                ? GovernedLoopCoordinatorPriorEvidenceExpectation.NotFound
                : GovernedLoopCoordinatorPriorEvidenceExpectation.Existing;
            var result = await store.TryAcquireAsync(Acquisition(
                ownership,
                expectation,
                Environment.GetEnvironmentVariable(CrossProcessExpectedOwnership),
                Environment.GetEnvironmentVariable(CrossProcessExpectedHeartbeat)));
            status = result!.Status.ToString();
        }

        await File.WriteAllTextAsync(output, status);
    }

    [Theory]
    [InlineData(GovernedLoopSleepStorePersistenceBoundary.PrecursorCreated, GovernedLoopCoordinatorAcquisitionStatus.Acquired)]
    [InlineData(GovernedLoopSleepStorePersistenceBoundary.Staged, GovernedLoopCoordinatorAcquisitionStatus.Acquired)]
    [InlineData(GovernedLoopSleepStorePersistenceBoundary.Publishing, GovernedLoopCoordinatorAcquisitionStatus.Acquired)]
    [InlineData(GovernedLoopSleepStorePersistenceBoundary.Published, GovernedLoopCoordinatorAcquisitionStatus.Duplicate)]
    public async Task Crash_boundary_unavailability_is_exactly_recoverable(
        GovernedLoopSleepStorePersistenceBoundary boundary,
        GovernedLoopCoordinatorAcquisitionStatus retryStatus)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var request = Acquisition();
        var interrupted = new GovernedLoopCoordinatorEvidenceStore(paths, new GovernedLoopCoordinatorEvidenceStoreOptions
        {
            DurableBoundaryObserver = observed =>
            {
                if (observed == boundary)
                {
                    throw new IOException("simulated process loss");
                }
            },
        });

        var first = await interrupted.TryAcquireAsync(request);
        var retry = await new GovernedLoopCoordinatorEvidenceStore(paths).TryAcquireAsync(request);
        Assert.Equal(GovernedLoopCoordinatorAcquisitionStatus.Unavailable, first!.Status);
        Assert.Equal(retryStatus, retry!.Status);
    }

    [Fact]
    public async Task Cancellation_while_waiting_for_the_workspace_lease_is_propagated_by_every_operation()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var acquisition = Acquisition();
        var waitingStore = new GovernedLoopCoordinatorEvidenceStore(paths);
        Assert.Equal(GovernedLoopCoordinatorAcquisitionStatus.Acquired, (await waitingStore.TryAcquireAsync(acquisition))!.Status);
        // https://github.com/Jacob-J-Thomas/agenthome-poc/issues/505
        // Own the real cross-process lock directly so fixture readiness never depends on ThreadPool scheduling.
        using var externalLock = CrossProcessExclusiveFileLock.Acquire(Path.Combine(StoreRoot(paths), ".queue.lock"));
        var (heartbeat, lifecycle, failure) = MutationRequests(acquisition);

        await AssertCancellationAsync(token => waitingStore.ReadAsync(acquisition.ProposedOwnership.CoordinatorId, token));
        await AssertCancellationAsync(token => waitingStore.TryAcquireAsync(acquisition, token));
        await AssertCancellationAsync(token => waitingStore.RenewHeartbeatAsync(heartbeat, token));
        await AssertCancellationAsync(token => waitingStore.AppendLifecycleAsync(lifecycle, token));
        await AssertCancellationAsync(token => waitingStore.AppendFailureAsync(failure, token));

        externalLock.Dispose();
        Assert.Equal(GovernedLoopCoordinatorReadStatus.Found, (await waitingStore.ReadAsync(acquisition.ProposedOwnership.CoordinatorId))!.Status);

        static async Task AssertCancellationAsync(Func<CancellationToken, Task> operation)
        {
            using var cancellation = new CancellationTokenSource();
            var pending = operation(cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
    }

    [Fact]
    public async Task Malformed_noncanonical_duplicate_and_bounded_ledgers_fail_closed()
    {
        await AssertCorruptAsync(bytes => [.. bytes, (byte)' ']);
        await AssertCorruptAsync(bytes => [0xEF, 0xBB, 0xBF, .. bytes]);
        await AssertCorruptAsync(bytes => MutateJson(bytes, root => root["schemaVersion"] = 2));
        await AssertCorruptAsync(bytes => MutateJson(bytes, root => root["entries"] = new JsonObject()));
        await AssertCorruptAsync(bytes => MutateJson(bytes, root => root["entries"] = new JsonArray()));
        await AssertCorruptAsync(bytes => MutateJson(bytes, root =>
        {
            var entries = (JsonArray)root["entries"]!;
            entries.Add(entries[0]!.DeepClone());
        }));
        await AssertCorruptAsync(bytes => MutateJson(bytes, root =>
        {
            var ownerships = (JsonArray)((JsonObject)((JsonArray)root["entries"]!)[0]!)["ownerships"]!;
            ownerships.Add(ownerships[0]!.DeepClone());
        }));

        using var readBoundWorkspace = new TestWorkspace();
        var readBoundPaths = new WorkspacePaths(readBoundWorkspace.RootPath);
        var readBoundStore = new GovernedLoopCoordinatorEvidenceStore(readBoundPaths);
        await readBoundStore.TryAcquireAsync(Acquisition());
        var secondOwner = GovernedLoopSleepContractTestFixture.Ownership(
            coordinatorId: "second-coordinator",
            ownerId: "second-owner");
        await readBoundStore.TryAcquireAsync(Acquisition(secondOwner));
        Assert.Equal(
            GovernedLoopCoordinatorReadStatus.Corrupt,
            (await new GovernedLoopCoordinatorEvidenceStore(readBoundPaths, new GovernedLoopCoordinatorEvidenceStoreOptions
            {
                MaxCoordinators = 1,
            }).ReadAsync("background-coordinator"))!.Status);
        Assert.Equal(
            GovernedLoopCoordinatorReadStatus.Corrupt,
            (await new GovernedLoopCoordinatorEvidenceStore(readBoundPaths, new GovernedLoopCoordinatorEvidenceStoreOptions
            {
                MaxCatalogUtf8Bytes = 128,
            }).ReadAsync("background-coordinator"))!.Status);

        using var generationWorkspace = new TestWorkspace();
        var generationPaths = new WorkspacePaths(generationWorkspace.RootPath);
        await new GovernedLoopCoordinatorEvidenceStore(generationPaths).TryAcquireAsync(Acquisition());
        var generationOne = LatestLedger(generationPaths);
        File.Move(generationOne, Path.Combine(StoreRoot(generationPaths), "ledger-0000000000000000002.json"));
        Assert.Equal(
            GovernedLoopCoordinatorReadStatus.Corrupt,
            (await new GovernedLoopCoordinatorEvidenceStore(generationPaths).ReadAsync("background-coordinator"))!.Status);
    }

    [Theory]
    [InlineData("single-family")]
    [InlineData("aggregate")]
    public async Task Evidence_array_bounds_are_preflighted_before_materialization(string shape)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        await new GovernedLoopCoordinatorEvidenceStore(paths).TryAcquireAsync(Acquisition());
        var ledger = LatestLedger(paths);
        var bytes = await File.ReadAllBytesAsync(ledger);
        await File.WriteAllBytesAsync(ledger, MutateJson(bytes, root =>
        {
            var entry = (JsonObject)((JsonArray)root["entries"]!)[0]!;
            if (string.Equals(shape, "single-family", StringComparison.Ordinal))
            {
                var ownerships = (JsonArray)entry["ownerships"]!;
                var clone = ownerships[0]!.DeepClone();
                for (var index = 0; index < 8; index++)
                {
                    ownerships.Add(clone.DeepClone());
                }
            }
            else
            {
                var lifecycles = (JsonArray)entry["lifecycles"]!;
                var clone = lifecycles[0]!.DeepClone();
                for (var index = 0; index < 8; index++)
                {
                    lifecycles.Add(clone.DeepClone());
                }
            }
        }));

        var bounded = new GovernedLoopCoordinatorEvidenceStore(paths, new GovernedLoopCoordinatorEvidenceStoreOptions
        {
            MaxEvidenceItemsPerCoordinator = 10,
        });
        Assert.Equal(
            GovernedLoopCoordinatorReadStatus.Corrupt,
            (await bounded.ReadAsync("background-coordinator"))!.Status);
    }

    [Fact]
    public async Task Cross_owner_evidence_order_must_remain_monotonic()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new GovernedLoopCoordinatorEvidenceStore(paths);
        var initial = Acquisition();
        await store.TryAcquireAsync(initial);
        var successor = GovernedLoopSleepContractTestFixture.Ownership(
            ownerId: "process-owner-2",
            ownershipEpoch: 2,
            acquiredAtUtc: initial.InitialHeartbeat.LeaseExpiresAtUtc);
        await store.TryAcquireAsync(Acquisition(
            successor,
            GovernedLoopCoordinatorPriorEvidenceExpectation.Existing,
            initial.ProposedOwnership.ContentHash,
            initial.InitialHeartbeat.ContentHash));
        var ledger = LatestLedger(paths);
        var bytes = await File.ReadAllBytesAsync(ledger);
        await File.WriteAllBytesAsync(ledger, MutateJson(bytes, root =>
        {
            var entry = (JsonObject)((JsonArray)root["entries"]!)[0]!;
            var lifecycles = (JsonArray)entry["lifecycles"]!;
            var first = lifecycles[0]!.DeepClone();
            lifecycles[0] = lifecycles[1]!.DeepClone();
            lifecycles[1] = first;
        }));

        Assert.Equal(
            GovernedLoopCoordinatorReadStatus.Corrupt,
            (await new GovernedLoopCoordinatorEvidenceStore(paths).ReadAsync("background-coordinator"))!.Status);
    }

    [Theory]
    [InlineData("ownership-epoch")]
    [InlineData("lifecycle-status")]
    [InlineData("lifecycle-owner")]
    [InlineData("heartbeat-sequence")]
    [InlineData("failures-shape")]
    [InlineData("failure-kind")]
    [InlineData("entry-coordinator")]
    [InlineData("ownership-integrity")]
    [InlineData("initial-lifecycle")]
    public async Task Malformed_nested_coordinator_evidence_fails_closed(string mutation)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new GovernedLoopCoordinatorEvidenceStore(paths);
        var acquisition = Acquisition();
        await store.TryAcquireAsync(acquisition);
        if (string.Equals(mutation, "failure-kind", StringComparison.Ordinal))
        {
            var failure = GovernedLoopSleepContractTestFixture.Failure(
                ownership: acquisition.ProposedOwnership,
                occurredAtUtc: acquisition.StartingLifecycle.UpdatedAtUtc.AddSeconds(1));
            await store.AppendFailureAsync(new(
                acquisition.ProposedOwnership,
                acquisition.ProposedOwnership.ContentHash,
                GovernedLoopCoordinatorPriorFailureExpectation.None,
                0,
                null,
                failure));
        }

        var ledger = LatestLedger(paths);
        var bytes = await File.ReadAllBytesAsync(ledger);
        await File.WriteAllBytesAsync(ledger, MutateJson(bytes, root =>
        {
            var entry = (JsonObject)((JsonArray)root["entries"]!)[0]!;
            var ownership = (JsonObject)((JsonArray)entry["ownerships"]!)[0]!;
            var lifecycle = (JsonObject)((JsonArray)entry["lifecycles"]!)[0]!;
            var heartbeat = (JsonObject)((JsonArray)entry["heartbeats"]!)[0]!;
            switch (mutation)
            {
                case "ownership-epoch":
                    ownership["ownershipEpoch"] = "one";
                    break;
                case "lifecycle-status":
                    lifecycle["status"] = "unknown";
                    break;
                case "lifecycle-owner":
                    lifecycle["ownershipHash"] = GovernedLoopSleepContractTestFixture.Hash('0');
                    break;
                case "heartbeat-sequence":
                    heartbeat["heartbeatSequence"] = "one";
                    break;
                case "failures-shape":
                    entry["failures"] = new JsonObject();
                    break;
                case "failure-kind":
                    ((JsonObject)((JsonArray)entry["failures"]!)[0]!)["kind"] = "unknown";
                    break;
                case "entry-coordinator":
                    entry["coordinatorId"] = 7;
                    break;
                case "ownership-integrity":
                    {
                        var replacementHash = GovernedLoopSleepContractTestFixture.Hash('0');
                        ownership["contentHash"] = replacementHash;
                        lifecycle["ownershipHash"] = replacementHash;
                        heartbeat["ownershipHash"] = replacementHash;
                        break;
                    }
                case "initial-lifecycle":
                    lifecycle["status"] = "running";
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(mutation));
            }
        }));

        Assert.Equal(
            GovernedLoopCoordinatorReadStatus.Corrupt,
            (await new GovernedLoopCoordinatorEvidenceStore(paths).ReadAsync(
                acquisition.ProposedOwnership.CoordinatorId))!.Status);
    }

    [Fact]
    public async Task Invalid_requests_and_options_fail_before_mutation()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new GovernedLoopCoordinatorEvidenceStore(paths);
        Assert.Equal(GovernedLoopCoordinatorReadStatus.Corrupt, (await store.ReadAsync("BAD ID"))!.Status);
        Assert.Equal(GovernedLoopCoordinatorReadStatus.NotFound, (await store.ReadAsync("background-coordinator"))!.Status);
        var request = Acquisition();
        var malformed = request with { ExpectedOwnershipHash = GovernedLoopSleepContractTestFixture.Hash('0') };
        Assert.Equal(GovernedLoopCoordinatorAcquisitionStatus.Corrupt, (await store.TryAcquireAsync(malformed))!.Status);
        var (heartbeat, lifecycle, failure) = MutationRequests(request);
        Assert.Equal(GovernedLoopCoordinatorHeartbeatMutationStatus.OwnershipLost, (await store.RenewHeartbeatAsync(heartbeat))!.Status);
        Assert.Equal(GovernedLoopCoordinatorLifecycleMutationStatus.OwnershipLost, (await store.AppendLifecycleAsync(lifecycle))!.Status);
        Assert.Equal(GovernedLoopCoordinatorFailureMutationStatus.OwnershipLost, (await store.AppendFailureAsync(failure))!.Status);
        Assert.Equal(
            GovernedLoopCoordinatorHeartbeatMutationStatus.Corrupt,
            (await store.RenewHeartbeatAsync(heartbeat with { ExpectedOwnershipHash = "bad" }))!.Status);
        Assert.Equal(
            GovernedLoopCoordinatorLifecycleMutationStatus.Corrupt,
            (await store.AppendLifecycleAsync(lifecycle with { ExpectedOwnershipHash = "bad" }))!.Status);
        Assert.Equal(
            GovernedLoopCoordinatorFailureMutationStatus.Corrupt,
            (await store.AppendFailureAsync(failure with { ExpectedOwnershipHash = "bad" }))!.Status);
        Assert.Throws<ArgumentOutOfRangeException>(() => new GovernedLoopCoordinatorEvidenceStore(paths, new GovernedLoopCoordinatorEvidenceStoreOptions { MaxCoordinators = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GovernedLoopCoordinatorEvidenceStore(paths, new GovernedLoopCoordinatorEvidenceStoreOptions { MaxEvidenceItemsPerCoordinator = 9 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GovernedLoopCoordinatorEvidenceStore(paths, new GovernedLoopCoordinatorEvidenceStoreOptions { MaxCatalogUtf8Bytes = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GovernedLoopCoordinatorEvidenceStore(paths, new GovernedLoopCoordinatorEvidenceStoreOptions { MaxDurabilityArtifacts = 0 }));
    }

    [Fact]
    public async Task Acquisition_and_mutation_statuses_fail_closed_at_every_optimistic_fence()
    {
        using var missingWorkspace = new TestWorkspace();
        var missingPaths = new WorkspacePaths(missingWorkspace.RootPath);
        var missingStore = new GovernedLoopCoordinatorEvidenceStore(missingPaths);
        var missingOwner = GovernedLoopSleepContractTestFixture.Ownership(
            ownerId: "process-owner-2",
            ownershipEpoch: 2,
            acquiredAtUtc: GovernedLoopSleepContractTestFixture.PublishedAtUtc.AddMinutes(2));
        var missingPrior = Acquisition(
            missingOwner,
            GovernedLoopCoordinatorPriorEvidenceExpectation.Existing,
            GovernedLoopSleepContractTestFixture.Hash('1'),
            GovernedLoopSleepContractTestFixture.Hash('2'));
        Assert.Equal(GovernedLoopCoordinatorAcquisitionStatus.Conflict, (await missingStore.TryAcquireAsync(missingPrior))!.Status);

        using var capacityWorkspace = new TestWorkspace();
        var capacityPaths = new WorkspacePaths(capacityWorkspace.RootPath);
        var capacityStore = new GovernedLoopCoordinatorEvidenceStore(capacityPaths, new GovernedLoopCoordinatorEvidenceStoreOptions
        {
            MaxCoordinators = 1,
        });
        await capacityStore.TryAcquireAsync(Acquisition());
        var secondCoordinator = GovernedLoopSleepContractTestFixture.Ownership(
            coordinatorId: "second-coordinator",
            ownerId: "second-owner");
        Assert.Equal(
            GovernedLoopCoordinatorAcquisitionStatus.Conflict,
            (await capacityStore.TryAcquireAsync(Acquisition(secondCoordinator)))!.Status);

        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new GovernedLoopCoordinatorEvidenceStore(paths);
        var initial = Acquisition();
        await store.TryAcquireAsync(initial);
        var successorOwner = GovernedLoopSleepContractTestFixture.Ownership(
            ownerId: "process-owner-2",
            ownershipEpoch: 2,
            acquiredAtUtc: initial.InitialHeartbeat.LeaseExpiresAtUtc);
        var staleExpectation = Acquisition(
            successorOwner,
            GovernedLoopCoordinatorPriorEvidenceExpectation.Existing,
            GovernedLoopSleepContractTestFixture.Hash('1'),
            GovernedLoopSleepContractTestFixture.Hash('2'));
        Assert.Equal(GovernedLoopCoordinatorAcquisitionStatus.Conflict, (await store.TryAcquireAsync(staleExpectation))!.Status);
        var skippedEpoch = GovernedLoopSleepContractTestFixture.Ownership(
            ownerId: "process-owner-3",
            ownershipEpoch: 3,
            acquiredAtUtc: initial.InitialHeartbeat.LeaseExpiresAtUtc);
        Assert.Equal(
            GovernedLoopCoordinatorAcquisitionStatus.Corrupt,
            (await store.TryAcquireAsync(Acquisition(
                skippedEpoch,
                GovernedLoopCoordinatorPriorEvidenceExpectation.Existing,
                initial.ProposedOwnership.ContentHash,
                initial.InitialHeartbeat.ContentHash)))!.Status);

        var (heartbeat, lifecycle, failure) = MutationRequests(initial);
        var lateHeartbeat = GovernedLoopSleepContractTestFixture.Heartbeat(
            2,
            initial.ProposedOwnership,
            initial.InitialHeartbeat.LeaseExpiresAtUtc,
            initial.InitialHeartbeat.LeaseExpiresAtUtc.AddMinutes(1));
        Assert.Equal(
            GovernedLoopCoordinatorHeartbeatMutationStatus.Corrupt,
            (await store.RenewHeartbeatAsync(new(
                initial.ProposedOwnership,
                initial.ProposedOwnership.ContentHash,
                heartbeat.ExpectedHeartbeatSequence,
                heartbeat.ExpectedHeartbeatHash,
                lateHeartbeat)))!.Status);
        Assert.Equal(
            GovernedLoopCoordinatorLifecycleMutationStatus.Conflict,
            (await store.AppendLifecycleAsync(lifecycle with { ExpectedLifecycleHash = GovernedLoopSleepContractTestFixture.Hash('0') }))!.Status);
        var repeatedStarting = GovernedLoopSleepContractTestFixture.Lifecycle(
            GovernedLoopCoordinatorStatus.Starting,
            2,
            initial.ProposedOwnership,
            initial.StartingLifecycle.UpdatedAtUtc.AddSeconds(1));
        Assert.Equal(
            GovernedLoopCoordinatorLifecycleMutationStatus.Corrupt,
            (await store.AppendLifecycleAsync(new(
                initial.ProposedOwnership,
                initial.ProposedOwnership.ContentHash,
                lifecycle.ExpectedLifecycleVersion,
                lifecycle.ExpectedLifecycleHash,
                repeatedStarting)))!.Status);
        var unexpectedPriorFailure = GovernedLoopSleepContractTestFixture.Failure(
            2,
            initial.ProposedOwnership,
            occurredAtUtc: initial.StartingLifecycle.UpdatedAtUtc.AddSeconds(2));
        Assert.Equal(
            GovernedLoopCoordinatorFailureMutationStatus.Conflict,
            (await store.AppendFailureAsync(new(
                initial.ProposedOwnership,
                initial.ProposedOwnership.ContentHash,
                GovernedLoopCoordinatorPriorFailureExpectation.Existing,
                1,
                GovernedLoopSleepContractTestFixture.Hash('0'),
                unexpectedPriorFailure)))!.Status);
        Assert.Equal(GovernedLoopCoordinatorFailureMutationStatus.Appended, (await store.AppendFailureAsync(failure))!.Status);
        var regressedFailure = GovernedLoopSleepContractTestFixture.Failure(
            2,
            initial.ProposedOwnership,
            occurredAtUtc: failure.ProposedFailure.OccurredAtUtc.AddTicks(-1));
        Assert.Equal(
            GovernedLoopCoordinatorFailureMutationStatus.Corrupt,
            (await store.AppendFailureAsync(new(
                initial.ProposedOwnership,
                initial.ProposedOwnership.ContentHash,
                GovernedLoopCoordinatorPriorFailureExpectation.Existing,
                1,
                failure.ProposedFailure.ContentHash,
                regressedFailure)))!.Status);

        using var boundedWorkspace = new TestWorkspace();
        var boundedPaths = new WorkspacePaths(boundedWorkspace.RootPath);
        var boundedStore = new GovernedLoopCoordinatorEvidenceStore(boundedPaths, new GovernedLoopCoordinatorEvidenceStoreOptions
        {
            MaxEvidenceItemsPerCoordinator = 10,
        });
        var boundedInitial = Acquisition();
        await boundedStore.TryAcquireAsync(boundedInitial);
        var (boundedHeartbeat, boundedLifecycle, boundedFailure) = MutationRequests(boundedInitial);
        Assert.Equal(GovernedLoopCoordinatorLifecycleMutationStatus.Appended, (await boundedStore.AppendLifecycleAsync(boundedLifecycle))!.Status);
        Assert.Equal(GovernedLoopCoordinatorFailureMutationStatus.Appended, (await boundedStore.AppendFailureAsync(boundedFailure))!.Status);
        Assert.Equal(GovernedLoopCoordinatorHeartbeatMutationStatus.Renewed, (await boundedStore.RenewHeartbeatAsync(boundedHeartbeat))!.Status);
        var boundedFailure2 = GovernedLoopSleepContractTestFixture.Failure(
            2,
            boundedInitial.ProposedOwnership,
            occurredAtUtc: boundedFailure.ProposedFailure.OccurredAtUtc.AddSeconds(1));
        Assert.Equal(
            GovernedLoopCoordinatorFailureMutationStatus.Appended,
            (await boundedStore.AppendFailureAsync(new(
                boundedInitial.ProposedOwnership,
                boundedInitial.ProposedOwnership.ContentHash,
                GovernedLoopCoordinatorPriorFailureExpectation.Existing,
                boundedFailure.ProposedFailure.FailureSequence,
                boundedFailure.ProposedFailure.ContentHash,
                boundedFailure2)))!.Status);
        var boundedFailure3 = GovernedLoopSleepContractTestFixture.Failure(
            3,
            boundedInitial.ProposedOwnership,
            occurredAtUtc: boundedFailure2.OccurredAtUtc.AddSeconds(1));
        Assert.Equal(
            GovernedLoopCoordinatorFailureMutationStatus.Appended,
            (await boundedStore.AppendFailureAsync(new(
                boundedInitial.ProposedOwnership,
                boundedInitial.ProposedOwnership.ContentHash,
                GovernedLoopCoordinatorPriorFailureExpectation.Existing,
                boundedFailure2.FailureSequence,
                boundedFailure2.ContentHash,
                boundedFailure3)))!.Status);
        var boundedSuccessor = GovernedLoopSleepContractTestFixture.Ownership(
            ownerId: "process-owner-2",
            ownershipEpoch: 2,
            acquiredAtUtc: boundedHeartbeat.ProposedHeartbeat.LeaseExpiresAtUtc);
        Assert.Equal(
            GovernedLoopCoordinatorAcquisitionStatus.Conflict,
            (await boundedStore.TryAcquireAsync(Acquisition(
                boundedSuccessor,
                GovernedLoopCoordinatorPriorEvidenceExpectation.Existing,
                boundedInitial.ProposedOwnership.ContentHash,
                boundedHeartbeat.ProposedHeartbeat.ContentHash)))!.Status);
        var nextLifecycle = GovernedLoopSleepContractTestFixture.Lifecycle(
            GovernedLoopCoordinatorStatus.Stopping,
            3,
            boundedInitial.ProposedOwnership,
            boundedLifecycle.ProposedLifecycle.UpdatedAtUtc.AddSeconds(1));
        var stopping = await boundedStore.AppendLifecycleAsync(new(
            boundedInitial.ProposedOwnership,
            boundedInitial.ProposedOwnership.ContentHash,
            boundedLifecycle.ProposedLifecycle.LifecycleVersion,
            boundedLifecycle.ProposedLifecycle.ContentHash,
            nextLifecycle));
        var stopped = GovernedLoopSleepContractTestFixture.Lifecycle(
            GovernedLoopCoordinatorStatus.Stopped,
            4,
            boundedInitial.ProposedOwnership,
            nextLifecycle.UpdatedAtUtc.AddSeconds(1));
        var terminal = await boundedStore.AppendLifecycleAsync(new(
            boundedInitial.ProposedOwnership,
            boundedInitial.ProposedOwnership.ContentHash,
            nextLifecycle.LifecycleVersion,
            nextLifecycle.ContentHash,
            stopped));
        var overflowFailure = GovernedLoopSleepContractTestFixture.Failure(
            4,
            boundedInitial.ProposedOwnership,
            occurredAtUtc: stopped.UpdatedAtUtc.AddSeconds(1));

        Assert.Equal(GovernedLoopCoordinatorLifecycleMutationStatus.Appended, stopping!.Status);
        Assert.Equal(GovernedLoopCoordinatorLifecycleMutationStatus.Appended, terminal!.Status);
        Assert.Equal(
            GovernedLoopCoordinatorFailureMutationStatus.Conflict,
            (await boundedStore.AppendFailureAsync(new(
                boundedInitial.ProposedOwnership,
                boundedInitial.ProposedOwnership.ContentHash,
                GovernedLoopCoordinatorPriorFailureExpectation.Existing,
                boundedFailure3.FailureSequence,
                boundedFailure3.ContentHash,
                overflowFailure)))!.Status);
    }

    [Fact]
    public async Task Mutation_boundary_failure_maps_every_append_operation_to_unavailable()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var acquisition = Acquisition();
        await new GovernedLoopCoordinatorEvidenceStore(paths).TryAcquireAsync(acquisition);
        var store = new GovernedLoopCoordinatorEvidenceStore(paths, new GovernedLoopCoordinatorEvidenceStoreOptions
        {
            DurableBoundaryObserver = boundary =>
            {
                if (boundary == GovernedLoopSleepStorePersistenceBoundary.PrecursorCreated)
                {
                    throw new IOException("simulated coordinator evidence outage");
                }
            },
        });
        var (heartbeat, lifecycle, failure) = MutationRequests(acquisition);

        Assert.Equal(GovernedLoopCoordinatorHeartbeatMutationStatus.Unavailable, (await store.RenewHeartbeatAsync(heartbeat))!.Status);
        Assert.Equal(GovernedLoopCoordinatorLifecycleMutationStatus.Unavailable, (await store.AppendLifecycleAsync(lifecycle))!.Status);
        Assert.Equal(GovernedLoopCoordinatorFailureMutationStatus.Unavailable, (await store.AppendFailureAsync(failure))!.Status);
    }

    [Fact]
    public async Task Unix_symlink_substitution_maps_every_public_store_operation_to_corrupt()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var outside = workspace.File("outside-loops");
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(paths.AgentPath);
        File.CreateSymbolicLink(paths.AgentFile("loops"), outside);
        var store = new GovernedLoopCoordinatorEvidenceStore(paths);
        var acquisition = Acquisition();
        var (heartbeat, lifecycle, failure) = MutationRequests(acquisition);

        Assert.Equal(GovernedLoopCoordinatorReadStatus.Corrupt, (await store.ReadAsync("background-coordinator"))!.Status);
        Assert.Equal(GovernedLoopCoordinatorAcquisitionStatus.Corrupt, (await store.TryAcquireAsync(acquisition))!.Status);
        Assert.Equal(GovernedLoopCoordinatorHeartbeatMutationStatus.Corrupt, (await store.RenewHeartbeatAsync(heartbeat))!.Status);
        Assert.Equal(GovernedLoopCoordinatorLifecycleMutationStatus.Corrupt, (await store.AppendLifecycleAsync(lifecycle))!.Status);
        Assert.Equal(GovernedLoopCoordinatorFailureMutationStatus.Corrupt, (await store.AppendFailureAsync(failure))!.Status);
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
    }

    private static GovernedLoopCoordinatorAcquisitionRequest Acquisition(
        GovernedLoopCoordinatorOwnership? ownership = null,
        GovernedLoopCoordinatorPriorEvidenceExpectation expectation = GovernedLoopCoordinatorPriorEvidenceExpectation.NotFound,
        string? expectedOwnershipHash = null,
        string? expectedHeartbeatHash = null)
    {
        var selected = ownership ?? GovernedLoopSleepContractTestFixture.Ownership();
        var lifecycle = GovernedLoopSleepContractTestFixture.Lifecycle(
            GovernedLoopCoordinatorStatus.Starting,
            ownership: selected,
            updatedAtUtc: selected.AcquiredAtUtc);
        var heartbeat = GovernedLoopSleepContractTestFixture.Heartbeat(
            ownership: selected,
            recordedAtUtc: selected.AcquiredAtUtc,
            leaseExpiresAtUtc: selected.AcquiredAtUtc.AddMinutes(1));
        return new GovernedLoopCoordinatorAcquisitionRequest(
            expectation,
            expectedOwnershipHash,
            expectedHeartbeatHash,
            selected,
            lifecycle,
            heartbeat);
    }

    private static (
        GovernedLoopCoordinatorHeartbeatMutationRequest Heartbeat,
        GovernedLoopCoordinatorLifecycleMutationRequest Lifecycle,
        GovernedLoopCoordinatorFailureMutationRequest Failure) MutationRequests(
            GovernedLoopCoordinatorAcquisitionRequest acquisition)
    {
        var ownership = acquisition.ProposedOwnership;
        var heartbeat = GovernedLoopSleepContractTestFixture.Heartbeat(
            2,
            ownership,
            acquisition.InitialHeartbeat.RecordedAtUtc.AddSeconds(1),
            acquisition.InitialHeartbeat.LeaseExpiresAtUtc.AddMinutes(1));
        var lifecycle = GovernedLoopSleepContractTestFixture.Lifecycle(
            GovernedLoopCoordinatorStatus.Running,
            2,
            ownership,
            acquisition.StartingLifecycle.UpdatedAtUtc.AddSeconds(1));
        var failure = GovernedLoopSleepContractTestFixture.Failure(
            ownership: ownership,
            occurredAtUtc: acquisition.StartingLifecycle.UpdatedAtUtc.AddSeconds(1));
        return (
            new(
                ownership,
                ownership.ContentHash,
                acquisition.InitialHeartbeat.HeartbeatSequence,
                acquisition.InitialHeartbeat.ContentHash,
                heartbeat),
            new(
                ownership,
                ownership.ContentHash,
                acquisition.StartingLifecycle.LifecycleVersion,
                acquisition.StartingLifecycle.ContentHash,
                lifecycle),
            new(
                ownership,
                ownership.ContentHash,
                GovernedLoopCoordinatorPriorFailureExpectation.None,
                0,
                null,
                failure));
    }

    private static async Task AssertCorruptAsync(Func<byte[], byte[]> mutation)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        await new GovernedLoopCoordinatorEvidenceStore(paths).TryAcquireAsync(Acquisition());
        var ledger = LatestLedger(paths);
        await File.WriteAllBytesAsync(ledger, mutation(await File.ReadAllBytesAsync(ledger)));
        Assert.Equal(
            GovernedLoopCoordinatorReadStatus.Corrupt,
            (await new GovernedLoopCoordinatorEvidenceStore(paths).ReadAsync("background-coordinator"))!.Status);
    }

    private static byte[] MutateJson(byte[] bytes, Action<JsonObject> mutation)
    {
        var root = JsonNode.Parse(bytes)!.AsObject();
        mutation(root);
        return Encoding.UTF8.GetBytes(root.ToJsonString());
    }

    private static string StoreRoot(WorkspacePaths paths)
        => paths.AgentFile(Path.Combine("loops", "execution", "coordinator"));

    private static string LatestLedger(WorkspacePaths paths)
        => Directory.EnumerateFiles(StoreRoot(paths), "ledger-*.json").Order(StringComparer.Ordinal).Last();

    private static async Task<string[]> RunCrossProcessRaceAsync(
        string workspace,
        string mode,
        string firstOwner,
        string secondOwner,
        long epoch = 1,
        DateTimeOffset? acquiredAtUtc = null,
        string? expectedOwnership = null,
        string? expectedHeartbeat = null)
    {
        var gate = Path.Combine(workspace, $"release-{mode}-owners");
        var firstReady = Path.Combine(workspace, $"first-{mode}-ready");
        var secondReady = Path.Combine(workspace, $"second-{mode}-ready");
        var firstOutput = Path.Combine(workspace, $"first-{mode}-output");
        var secondOutput = Path.Combine(workspace, $"second-{mode}-output");
        using var first = StartCrossProcessHost(
            workspace,
            gate,
            firstReady,
            firstOutput,
            mode,
            firstOwner,
            epoch,
            acquiredAtUtc ?? GovernedLoopSleepContractTestFixture.PublishedAtUtc,
            expectedOwnership,
            expectedHeartbeat);
        using var second = StartCrossProcessHost(
            workspace,
            gate,
            secondReady,
            secondOutput,
            mode,
            secondOwner,
            epoch,
            acquiredAtUtc ?? GovernedLoopSleepContractTestFixture.PublishedAtUtc,
            expectedOwnership,
            expectedHeartbeat);
        await Task.WhenAll(WaitForPathAsync(firstReady), WaitForPathAsync(secondReady));
        await File.WriteAllTextAsync(gate, "go");
        await Task.WhenAll(first.WaitForExitAsync(), second.WaitForExitAsync()).WaitAsync(TimeSpan.FromSeconds(30));
        await AssertProcessSucceededAsync(first);
        await AssertProcessSucceededAsync(second);
        return [await File.ReadAllTextAsync(firstOutput), await File.ReadAllTextAsync(secondOutput)];
    }

    private static Process StartCrossProcessHost(
        string workspace,
        string gate,
        string ready,
        string output,
        string mode,
        string ownerId,
        long epoch,
        DateTimeOffset acquiredAtUtc,
        string? expectedOwnership,
        string? expectedHeartbeat)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetTempPath(),
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // Exact Windows evidence proved these five ownership workers add no lines beyond the parent
        // lane, so they omit duplicate child coverage; see https://github.com/Jacob-J-Thomas/agenthome-poc/issues/422.
        Verification.CoverageChildProcessAssembly.AddCoordinationOnlyVstestArguments(
            startInfo,
            typeof(GovernedLoopCoordinatorEvidenceStoreTests).Assembly.Location,
            "EmbodySense.Core.Persistence.Tests.Loops.Execution.Sleep.GovernedLoopCoordinatorEvidenceStoreTests.Cross_process_coordinator_store_host");
        startInfo.Environment["DOTNET_ROLL_FORWARD"] = "Major";
        startInfo.Environment[CrossProcessWorkspace] = workspace;
        startInfo.Environment[CrossProcessGate] = gate;
        startInfo.Environment[CrossProcessReady] = ready;
        startInfo.Environment[CrossProcessOutput] = output;
        startInfo.Environment[CrossProcessMode] = mode;
        startInfo.Environment[CrossProcessOwnerId] = ownerId;
        startInfo.Environment[CrossProcessEpoch] = epoch.ToString(System.Globalization.CultureInfo.InvariantCulture);
        startInfo.Environment[CrossProcessAcquiredAt] = acquiredAtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        if (expectedOwnership is not null)
        {
            startInfo.Environment[CrossProcessExpectedOwnership] = expectedOwnership;
        }

        if (expectedHeartbeat is not null)
        {
            startInfo.Environment[CrossProcessExpectedHeartbeat] = expectedHeartbeat;
        }

        return Process.Start(startInfo) ?? throw new InvalidOperationException("Cross-process coordinator-store test host did not start.");
    }

    private static long ParseEpoch()
        => long.Parse(Environment.GetEnvironmentVariable(CrossProcessEpoch)!, System.Globalization.CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseAcquiredAt()
        => DateTimeOffset.ParseExact(
            Environment.GetEnvironmentVariable(CrossProcessAcquiredAt)!,
            "O",
            System.Globalization.CultureInfo.InvariantCulture);

    private static async Task WaitForPathAsync(string path)
    {
        var wait = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            Assert.True(wait.Elapsed < TimeSpan.FromSeconds(60), $"Cross-process coordinator host did not create `{path}`.");
            await Task.Delay(10);
        }
    }

    private static async Task AssertProcessSucceededAsync(Process process)
    {
        var standardError = await process.StandardError.ReadToEndAsync();
        var standardOutput = await process.StandardOutput.ReadToEndAsync();
        Assert.True(process.ExitCode == 0, standardError + Environment.NewLine + standardOutput);
    }
}
