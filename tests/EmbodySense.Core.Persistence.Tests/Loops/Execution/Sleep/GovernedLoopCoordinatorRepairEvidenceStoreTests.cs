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

public sealed class GovernedLoopCoordinatorRepairEvidenceStoreTests
{
    private static readonly string _workspaceId = "workspace-sha256:" + new string('a', 64);

    [Fact]
    public async Task Repair_append_rejects_a_live_lease_and_is_concurrently_exactly_replayable()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new GovernedLoopCoordinatorEvidenceStore(paths);
        var failed = await CreateFailedSnapshotAsync(store);
        var beforeExpiry = Repair(failed, failed.LatestHeartbeat.LeaseExpiresAtUtc.AddTicks(-1));
        var repair = Repair(failed, failed.LatestHeartbeat.LeaseExpiresAtUtc);

        var liveLease = await store.AppendAsync(beforeExpiry);
        var results = await Task.WhenAll(
            new GovernedLoopCoordinatorEvidenceStore(paths).AppendAsync(repair),
            new GovernedLoopCoordinatorEvidenceStore(paths).AppendAsync(repair));
        var retained = await new GovernedLoopCoordinatorEvidenceStore(paths).ReadAsync(
            failed.Ownership.CoordinatorId,
            failed.Ownership.ContentHash);

        Assert.Equal(GovernedLoopCoordinatorRepairMutationStatus.Conflict, liveLease!.Status);
        Assert.Single(results, item => item!.Status == GovernedLoopCoordinatorRepairMutationStatus.Appended);
        Assert.Single(results, item => item!.Status == GovernedLoopCoordinatorRepairMutationStatus.Duplicate);
        Assert.Equal(GovernedLoopCoordinatorRepairReadStatus.Found, retained!.Status);
        Assert.Equal(repair, retained.Disposition);
    }

    [Fact]
    public async Task Published_repair_response_loss_replays_exactly_after_process_recovery()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var failed = await CreateFailedSnapshotAsync(new GovernedLoopCoordinatorEvidenceStore(paths));
        var repair = Repair(failed, failed.LatestHeartbeat.LeaseExpiresAtUtc);
        var interrupted = new GovernedLoopCoordinatorEvidenceStore(paths, new GovernedLoopCoordinatorEvidenceStoreOptions
        {
            DurableBoundaryObserver = boundary =>
            {
                if (boundary == GovernedLoopSleepStorePersistenceBoundary.Published)
                {
                    throw new IOException("simulated response loss after repair publication");
                }
            }
        });

        var lostResponse = await interrupted.AppendAsync(repair);
        var replay = await new GovernedLoopCoordinatorEvidenceStore(paths).AppendAsync(repair);

        Assert.Equal(GovernedLoopCoordinatorRepairMutationStatus.Unavailable, lostResponse!.Status);
        Assert.Equal(GovernedLoopCoordinatorRepairMutationStatus.Duplicate, replay!.Status);
        Assert.Equal(repair, replay.Disposition);
    }

    [Fact]
    public async Task Repair_append_rejects_a_reused_operation_or_changed_failed_binding_without_overwriting_history()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new GovernedLoopCoordinatorEvidenceStore(paths);
        var failed = await CreateFailedSnapshotAsync(store);
        var repair = Repair(failed, failed.LatestHeartbeat.LeaseExpiresAtUtc);
        var changedOperation = GovernedLoopSleepContractHash.Apply(repair with { OperationId = "repair-operation-2", ContentHash = string.Empty });
        var changedFailure = GovernedLoopSleepContractHash.Apply(repair with { LatestFailureHash = new string('b', 64), ContentHash = string.Empty });

        var appended = await store.AppendAsync(repair);
        var operationConflict = await store.AppendAsync(changedOperation);
        var evidenceConflict = await store.AppendAsync(changedFailure);
        var retained = await store.ReadAsync(failed.Ownership.CoordinatorId, failed.Ownership.ContentHash);

        Assert.Equal(GovernedLoopCoordinatorRepairMutationStatus.Appended, appended!.Status);
        Assert.Equal(GovernedLoopCoordinatorRepairMutationStatus.Conflict, operationConflict!.Status);
        Assert.Equal(GovernedLoopCoordinatorRepairMutationStatus.Conflict, evidenceConflict!.Status);
        Assert.Equal(repair, retained!.Disposition);
    }

    [Fact]
    public async Task Persisted_repair_readiness_without_human_review_field_fails_closed()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new GovernedLoopCoordinatorEvidenceStore(paths);
        var failed = await CreateFailedSnapshotAsync(store);
        var repair = Repair(failed, failed.LatestHeartbeat.LeaseExpiresAtUtc);
        Assert.Equal(GovernedLoopCoordinatorRepairMutationStatus.Appended, (await store.AppendAsync(repair))!.Status);

        var ledgerDirectory = paths.AgentFile(Path.Combine("loops", "execution", "coordinator"));
        var ledger = Directory.EnumerateFiles(ledgerDirectory, "ledger-*.json").Order(StringComparer.Ordinal).Last();
        var root = JsonNode.Parse(await File.ReadAllBytesAsync(ledger))!.AsObject();
        var entry = (JsonObject)((JsonArray)root["entries"]!)[0]!;
        var persistedRepair = (JsonObject)((JsonArray)entry["repairs"]!)[0]!;
        ((JsonObject)persistedRepair["dependencyReadiness"]!).Remove("humanReviewReady");
        await File.WriteAllTextAsync(ledger, root.ToJsonString(), Encoding.UTF8);

        var read = await new GovernedLoopCoordinatorEvidenceStore(paths).ReadAsync(failed.Ownership.CoordinatorId);

        Assert.Equal(GovernedLoopCoordinatorReadStatus.Corrupt, read!.Status);
    }

    [Fact]
    public async Task Fenced_takeover_requires_retained_repair_and_admits_one_fresh_successor()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new GovernedLoopCoordinatorEvidenceStore(paths);
        var failed = await CreateFailedSnapshotAsync(store);
        var repair = Repair(failed, failed.LatestHeartbeat.LeaseExpiresAtUtc);
        var request = RepairAcquisition(failed, repair);

        var denied = await store.TryAcquireAfterRepairAsync(request);
        Assert.Equal(GovernedLoopCoordinatorAcquisitionStatus.Conflict, denied!.Status);
        Assert.Equal(GovernedLoopCoordinatorRepairMutationStatus.Appended, (await store.AppendAsync(repair))!.Status);

        var results = await Task.WhenAll(
            new GovernedLoopCoordinatorEvidenceStore(paths).TryAcquireAfterRepairAsync(request),
            new GovernedLoopCoordinatorEvidenceStore(paths).TryAcquireAfterRepairAsync(request));
        var current = await new GovernedLoopCoordinatorEvidenceStore(paths).ReadAsync(failed.Ownership.CoordinatorId);

        Assert.Single(results, item => item!.Status == GovernedLoopCoordinatorAcquisitionStatus.Acquired);
        Assert.Single(results, item => item!.Status == GovernedLoopCoordinatorAcquisitionStatus.Duplicate);
        Assert.Equal(2, current!.Snapshot!.Ownership.OwnershipEpoch);
        Assert.Equal(GovernedLoopCoordinatorStatus.Starting, current.Snapshot.LatestLifecycle.Status);
    }

    [Fact]
    public async Task Fenced_takeover_rejects_successor_ownership_recorded_before_its_repair_authority()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new GovernedLoopCoordinatorEvidenceStore(paths);
        var failed = await CreateFailedSnapshotAsync(store);
        var repair = Repair(failed, failed.LatestHeartbeat.LeaseExpiresAtUtc);
        Assert.Equal(GovernedLoopCoordinatorRepairMutationStatus.Appended, (await store.AppendAsync(repair))!.Status);
        var successor = GovernedLoopSleepContractTestFixture.Ownership(
            ownerId: "process-owner-2",
            ownershipEpoch: failed.Ownership.OwnershipEpoch + 1,
            acquiredAtUtc: repair.RecordedAtUtc.AddTicks(-1));
        var acquisition = new GovernedLoopCoordinatorAcquisitionRequest(
            GovernedLoopCoordinatorPriorEvidenceExpectation.Existing,
            failed.Ownership.ContentHash,
            failed.LatestHeartbeat.ContentHash,
            successor,
            GovernedLoopSleepContractTestFixture.Lifecycle(GovernedLoopCoordinatorStatus.Starting, ownership: successor, updatedAtUtc: successor.AcquiredAtUtc),
            GovernedLoopSleepContractTestFixture.Heartbeat(ownership: successor, recordedAtUtc: successor.AcquiredAtUtc, leaseExpiresAtUtc: successor.AcquiredAtUtc.AddMinutes(1)));

        var result = await store.TryAcquireAfterRepairAsync(new GovernedLoopCoordinatorRepairAcquisitionRequest(repair, acquisition));
        var retained = await store.ReadAsync(failed.Ownership.CoordinatorId);

        Assert.Equal(GovernedLoopCoordinatorAcquisitionStatus.Corrupt, result!.Status);
        Assert.Equal(failed.Ownership, retained!.Snapshot!.Ownership);
    }

    [Fact]
    public async Task Repair_allows_the_same_process_owner_to_start_one_new_fenced_epoch()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new GovernedLoopCoordinatorEvidenceStore(paths);
        var failed = await CreateFailedSnapshotAsync(store);
        var repair = Repair(failed, failed.LatestHeartbeat.LeaseExpiresAtUtc);
        var request = RepairAcquisition(failed, repair, failed.Ownership.OwnerId);
        Assert.Equal(GovernedLoopCoordinatorRepairMutationStatus.Appended, (await store.AppendAsync(repair))!.Status);

        var acquired = await store.TryAcquireAfterRepairAsync(request);

        Assert.Equal(GovernedLoopCoordinatorAcquisitionStatus.Acquired, acquired!.Status);
        Assert.Equal(failed.Ownership.OwnerId, acquired.Snapshot!.Ownership.OwnerId);
        Assert.Equal(failed.Ownership.OwnershipEpoch + 1, acquired.Snapshot.Ownership.OwnershipEpoch);
    }

    [Fact]
    public async Task Published_repair_acquisition_response_loss_reconciles_to_one_exact_successor()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new GovernedLoopCoordinatorEvidenceStore(paths);
        var failed = await CreateFailedSnapshotAsync(store);
        var repair = Repair(failed, failed.LatestHeartbeat.LeaseExpiresAtUtc);
        var request = RepairAcquisition(failed, repair);
        Assert.Equal(GovernedLoopCoordinatorRepairMutationStatus.Appended, (await store.AppendAsync(repair))!.Status);
        var interrupted = new GovernedLoopCoordinatorEvidenceStore(paths, new GovernedLoopCoordinatorEvidenceStoreOptions
        {
            DurableBoundaryObserver = boundary =>
            {
                if (boundary == GovernedLoopSleepStorePersistenceBoundary.Published)
                {
                    throw new IOException("simulated response loss after repaired acquisition publication");
                }
            }
        });

        var lostResponse = await interrupted.TryAcquireAfterRepairAsync(request);
        var replay = await new GovernedLoopCoordinatorEvidenceStore(paths).TryAcquireAfterRepairAsync(request);

        Assert.Equal(GovernedLoopCoordinatorAcquisitionStatus.Unavailable, lostResponse!.Status);
        Assert.Equal(GovernedLoopCoordinatorAcquisitionStatus.Duplicate, replay!.Status);
        Assert.Equal(2, replay.Snapshot!.Ownership.OwnershipEpoch);
    }

    private static async Task<GovernedLoopCoordinatorSnapshot> CreateFailedSnapshotAsync(GovernedLoopCoordinatorEvidenceStore store)
    {
        var initial = Acquisition();
        Assert.Equal(GovernedLoopCoordinatorAcquisitionStatus.Acquired, (await store.TryAcquireAsync(initial))!.Status);
        var failure = GovernedLoopSleepContractTestFixture.Failure(
            ownership: initial.ProposedOwnership,
            occurredAtUtc: initial.InitialHeartbeat.RecordedAtUtc.AddSeconds(1));
        Assert.Equal(
            GovernedLoopCoordinatorFailureMutationStatus.Appended,
            (await store.AppendFailureAsync(new GovernedLoopCoordinatorFailureMutationRequest(
                initial.ProposedOwnership,
                initial.ProposedOwnership.ContentHash,
                GovernedLoopCoordinatorPriorFailureExpectation.None,
                0,
                null,
                failure)))!.Status);
        var failed = GovernedLoopSleepContractTestFixture.Lifecycle(
            GovernedLoopCoordinatorStatus.Failed,
            2,
            initial.ProposedOwnership,
            failure.OccurredAtUtc.AddSeconds(1));
        Assert.Equal(
            GovernedLoopCoordinatorLifecycleMutationStatus.Appended,
            (await store.AppendLifecycleAsync(new GovernedLoopCoordinatorLifecycleMutationRequest(
                initial.ProposedOwnership,
                initial.ProposedOwnership.ContentHash,
                initial.StartingLifecycle.LifecycleVersion,
                initial.StartingLifecycle.ContentHash,
                failed)))!.Status);
        return (await store.ReadAsync(initial.ProposedOwnership.CoordinatorId))!.Snapshot!;
    }

    private static GovernedLoopCoordinatorRepairDisposition Repair(GovernedLoopCoordinatorSnapshot failed, DateTimeOffset recordedAtUtc)
    {
        var readiness = GovernedLoopSleepContractHash.Apply(new GovernedLoopCoordinatorRepairReadiness(
            1,
            _workspaceId,
            failed.Ownership.CoordinatorId,
            true,
            true,
            true,
            true,
            true,
            recordedAtUtc,
            string.Empty));
        return GovernedLoopSleepContractHash.Apply(new GovernedLoopCoordinatorRepairDisposition(
            1,
            _workspaceId,
            failed.Ownership.CoordinatorId,
            "repair-operation",
            "operator-1",
            failed.Ownership,
            failed.LatestLifecycle.ContentHash,
            failed.LatestHeartbeat.ContentHash,
            failed.LatestFailureHash!,
            readiness,
            recordedAtUtc,
            string.Empty));
    }

    private static GovernedLoopCoordinatorRepairAcquisitionRequest RepairAcquisition(
        GovernedLoopCoordinatorSnapshot failed,
        GovernedLoopCoordinatorRepairDisposition repair,
        string ownerId = "process-owner-2")
    {
        var ownership = GovernedLoopSleepContractTestFixture.Ownership(
            ownerId: ownerId,
            ownershipEpoch: failed.Ownership.OwnershipEpoch + 1,
            acquiredAtUtc: failed.LatestHeartbeat.LeaseExpiresAtUtc);
        var acquisition = new GovernedLoopCoordinatorAcquisitionRequest(
            GovernedLoopCoordinatorPriorEvidenceExpectation.Existing,
            failed.Ownership.ContentHash,
            failed.LatestHeartbeat.ContentHash,
            ownership,
            GovernedLoopSleepContractTestFixture.Lifecycle(GovernedLoopCoordinatorStatus.Starting, ownership: ownership, updatedAtUtc: ownership.AcquiredAtUtc),
            GovernedLoopSleepContractTestFixture.Heartbeat(ownership: ownership, recordedAtUtc: ownership.AcquiredAtUtc, leaseExpiresAtUtc: ownership.AcquiredAtUtc.AddMinutes(1)));
        return new GovernedLoopCoordinatorRepairAcquisitionRequest(repair, acquisition);
    }

    private static GovernedLoopCoordinatorAcquisitionRequest Acquisition()
    {
        var ownership = GovernedLoopSleepContractTestFixture.Ownership();
        return new GovernedLoopCoordinatorAcquisitionRequest(
            GovernedLoopCoordinatorPriorEvidenceExpectation.NotFound,
            null,
            null,
            ownership,
            GovernedLoopSleepContractTestFixture.Lifecycle(GovernedLoopCoordinatorStatus.Starting, ownership: ownership, updatedAtUtc: ownership.AcquiredAtUtc),
            GovernedLoopSleepContractTestFixture.Heartbeat(ownership: ownership, recordedAtUtc: ownership.AcquiredAtUtc, leaseExpiresAtUtc: ownership.AcquiredAtUtc.AddMinutes(1)));
    }
}
