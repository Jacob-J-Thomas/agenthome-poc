using EmbodySense.Core.Application.Loops.Posture;
using EmbodySense.Core.Application.Loops.Sleep;
using EmbodySense.Core.Application.Loops.Sleep.Models;
using EmbodySense.Core.Common.Loops.Execution.Sleep;
using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;
using EmbodySense.Core.Common.Loops.Posture.Models;

namespace EmbodySense.Core.Application.Tests.Loops.Sleep;

public sealed class GovernedLoopCoordinatorRepairServiceTests
{
    private static readonly DateTimeOffset _now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly string _workspaceId = "workspace-sha256:" + new string('a', 64);

    [Fact]
    public async Task Preview_and_submit_bind_current_authority_failed_evidence_and_all_family_readiness()
    {
        var fixture = new RepairFixture();

        var preview = await fixture.Service.PreviewAsync(new GovernedLoopCoordinatorRepairPreviewRequest("coordinator", "repair-operation"));
        var submitted = await fixture.Service.SubmitAsync(new GovernedLoopCoordinatorRepairSubmitRequest(preview.Disposition!));
        var replayed = await fixture.Service.SubmitAsync(new GovernedLoopCoordinatorRepairSubmitRequest(preview.Disposition!));

        Assert.Equal(GovernedLoopCoordinatorRepairPreviewStatus.Ready, preview.Status);
        Assert.Equal("operator", preview.Disposition!.ActorId);
        Assert.Equal(fixture.Snapshot.Ownership, preview.Disposition.FailedOwnership);
        Assert.Equal(GovernedLoopCoordinatorRepairSubmitStatus.Accepted, submitted.Status);
        Assert.Equal(GovernedLoopCoordinatorRepairSubmitStatus.Replayed, replayed.Status);
        Assert.Single(fixture.Repairs.Dispositions);
    }

    [Fact]
    public async Task Submit_refuses_stale_failure_and_nonready_dependency_evidence_without_mutation()
    {
        var fixture = new RepairFixture();
        var preview = await fixture.Service.PreviewAsync(new GovernedLoopCoordinatorRepairPreviewRequest("coordinator", "repair-operation"));
        fixture.Snapshot = fixture.Snapshot with { LatestFailureHash = Hash('d') };

        var stale = await fixture.Service.SubmitAsync(new GovernedLoopCoordinatorRepairSubmitRequest(preview.Disposition!));
        fixture.Snapshot = FixtureSnapshot();
        fixture.Dependencies.Readiness = Ready() with { WakeReady = false };
        fixture.Dependencies.Readiness = GovernedLoopSleepContractHash.Apply(fixture.Dependencies.Readiness);
        var unavailablePreview = await fixture.Service.PreviewAsync(new GovernedLoopCoordinatorRepairPreviewRequest("coordinator", "another-operation"));

        Assert.Equal(GovernedLoopCoordinatorRepairSubmitStatus.Conflict, stale.Status);
        Assert.Equal(GovernedLoopCoordinatorRepairPreviewStatus.Conflict, unavailablePreview.Status);
        Assert.Empty(fixture.Repairs.Dispositions);
    }

    [Fact]
    public async Task Preview_fails_closed_when_current_operator_is_denied()
    {
        var fixture = new RepairFixture(permitted: false);

        var preview = await fixture.Service.PreviewAsync(new GovernedLoopCoordinatorRepairPreviewRequest("coordinator", "repair-operation"));

        Assert.Equal(GovernedLoopCoordinatorRepairPreviewStatus.Unauthorized, preview.Status);
        Assert.Null(preview.Disposition);
    }

    [Fact]
    public async Task Submit_refuses_actor_turnover_and_current_dependency_regression_without_appending()
    {
        var fixture = new RepairFixture();
        var firstPreview = await fixture.Service.PreviewAsync(new GovernedLoopCoordinatorRepairPreviewRequest("coordinator", "repair-operation"));
        fixture.Authority.ActorId = "successor";

        var actorChanged = await fixture.Service.SubmitAsync(new GovernedLoopCoordinatorRepairSubmitRequest(firstPreview.Disposition!));

        fixture.Authority.ActorId = "operator";
        var secondPreview = await fixture.Service.PreviewAsync(new GovernedLoopCoordinatorRepairPreviewRequest("coordinator", "repair-operation-2"));
        fixture.Dependencies.Readiness = GovernedLoopSleepContractHash.Apply(fixture.Dependencies.Readiness with { TriggerReady = false });
        var dependencyChanged = await fixture.Service.SubmitAsync(new GovernedLoopCoordinatorRepairSubmitRequest(secondPreview.Disposition!));

        Assert.Equal(GovernedLoopCoordinatorRepairSubmitStatus.Unauthorized, actorChanged.Status);
        Assert.Equal(GovernedLoopCoordinatorRepairSubmitStatus.Conflict, dependencyChanged.Status);
        Assert.Empty(fixture.Repairs.Dispositions);
    }

    [Fact]
    public async Task Submit_replays_retained_repair_without_requiring_current_failed_evidence_or_dependency_readiness()
    {
        var fixture = new RepairFixture();
        var preview = await fixture.Service.PreviewAsync(new GovernedLoopCoordinatorRepairPreviewRequest("coordinator", "repair-operation"));
        var accepted = await fixture.Service.SubmitAsync(new GovernedLoopCoordinatorRepairSubmitRequest(preview.Disposition!));
        fixture.Evidence.Exception = new IOException("the repaired generation is no longer current");
        fixture.Dependencies.Exception = new IOException("current readiness cannot alter a retained outcome");

        var replayed = await fixture.Service.SubmitAsync(new GovernedLoopCoordinatorRepairSubmitRequest(preview.Disposition!));
        var changed = GovernedLoopSleepContractHash.Apply(preview.Disposition! with
        {
            OperationId = "changed-operation",
            ContentHash = string.Empty
        });
        var conflict = await fixture.Service.SubmitAsync(new GovernedLoopCoordinatorRepairSubmitRequest(changed));

        Assert.Equal(GovernedLoopCoordinatorRepairSubmitStatus.Accepted, accepted.Status);
        Assert.Equal(GovernedLoopCoordinatorRepairSubmitStatus.Replayed, replayed.Status);
        Assert.Equal(GovernedLoopCoordinatorRepairSubmitStatus.Conflict, conflict.Status);
        Assert.Single(fixture.Repairs.Dispositions);
    }

    [Fact]
    public async Task Preview_rejects_malformed_caller_held_operation_without_reading_authority()
    {
        var fixture = new RepairFixture();

        var preview = await fixture.Service.PreviewAsync(new GovernedLoopCoordinatorRepairPreviewRequest("coordinator", "invalid operation"));

        Assert.Equal(GovernedLoopCoordinatorRepairPreviewStatus.Invalid, preview.Status);
        Assert.Null(preview.Disposition);
    }

    [Fact]
    public async Task Preview_maps_unavailable_corrupt_and_not_found_authority_evidence_and_dependency_reads()
    {
        var unavailableAuthority = new RepairFixture();
        unavailableAuthority.Authority.Exception = new IOException("authority unavailable");
        var corruptEvidence = new RepairFixture();
        corruptEvidence.Evidence.Result = new GovernedLoopCoordinatorReadResult(GovernedLoopCoordinatorReadStatus.Corrupt);
        var missingEvidence = new RepairFixture();
        missingEvidence.Evidence.Result = new GovernedLoopCoordinatorReadResult(GovernedLoopCoordinatorReadStatus.NotFound);
        var unavailableDependencies = new RepairFixture();
        unavailableDependencies.Dependencies.Exception = new IOException("dependencies unavailable");

        var authority = await unavailableAuthority.Service.PreviewAsync(new GovernedLoopCoordinatorRepairPreviewRequest("coordinator", "repair-operation"));
        var corrupt = await corruptEvidence.Service.PreviewAsync(new GovernedLoopCoordinatorRepairPreviewRequest("coordinator", "repair-operation"));
        var missing = await missingEvidence.Service.PreviewAsync(new GovernedLoopCoordinatorRepairPreviewRequest("coordinator", "repair-operation"));
        var dependencies = await unavailableDependencies.Service.PreviewAsync(new GovernedLoopCoordinatorRepairPreviewRequest("coordinator", "repair-operation"));

        Assert.Equal(GovernedLoopCoordinatorRepairPreviewStatus.Unavailable, authority.Status);
        Assert.Equal(GovernedLoopCoordinatorRepairPreviewStatus.Corrupt, corrupt.Status);
        Assert.Equal(GovernedLoopCoordinatorRepairPreviewStatus.NotFound, missing.Status);
        Assert.Equal(GovernedLoopCoordinatorRepairPreviewStatus.Unavailable, dependencies.Status);
    }

    [Fact]
    public async Task Submit_rejects_invalid_or_foreign_dispositions_and_maps_safe_ledger_outcomes()
    {
        var invalidFixture = new RepairFixture();
        var invalid = await invalidFixture.Service.SubmitAsync(null!);
        var foreignFixture = new RepairFixture();
        var foreignPreview = await foreignFixture.Service.PreviewAsync(new GovernedLoopCoordinatorRepairPreviewRequest("coordinator", "repair-operation"));
        var foreignWorkspaceId = "workspace-sha256:" + new string('b', 64);
        var foreignReadiness = GovernedLoopSleepContractHash.Apply(foreignPreview.Disposition!.DependencyReadiness with
        {
            WorkspaceId = foreignWorkspaceId,
            ContentHash = string.Empty
        });
        var foreign = GovernedLoopSleepContractHash.Apply(foreignPreview.Disposition! with
        {
            WorkspaceId = foreignWorkspaceId,
            DependencyReadiness = foreignReadiness,
            ContentHash = string.Empty
        });
        var foreignResult = await foreignFixture.Service.SubmitAsync(new GovernedLoopCoordinatorRepairSubmitRequest(foreign));
        var corruptLedger = new RepairFixture();
        var corruptPreview = await corruptLedger.Service.PreviewAsync(new GovernedLoopCoordinatorRepairPreviewRequest("coordinator", "repair-operation"));
        corruptLedger.Repairs.ForcedResult = new GovernedLoopCoordinatorRepairMutationResult(GovernedLoopCoordinatorRepairMutationStatus.Corrupt);
        var corrupt = await corruptLedger.Service.SubmitAsync(new GovernedLoopCoordinatorRepairSubmitRequest(corruptPreview.Disposition!));
        var unavailableLedger = new RepairFixture();
        var unavailablePreview = await unavailableLedger.Service.PreviewAsync(new GovernedLoopCoordinatorRepairPreviewRequest("coordinator", "repair-operation"));
        unavailableLedger.Repairs.ForcedResult = new GovernedLoopCoordinatorRepairMutationResult(GovernedLoopCoordinatorRepairMutationStatus.Unavailable);
        var unavailable = await unavailableLedger.Service.SubmitAsync(new GovernedLoopCoordinatorRepairSubmitRequest(unavailablePreview.Disposition!));

        Assert.Equal(GovernedLoopCoordinatorRepairSubmitStatus.Invalid, invalid.Status);
        Assert.Equal(GovernedLoopCoordinatorRepairSubmitStatus.Conflict, foreignResult.Status);
        Assert.Equal(GovernedLoopCoordinatorRepairSubmitStatus.Corrupt, corrupt.Status);
        Assert.Equal(GovernedLoopCoordinatorRepairSubmitStatus.Unavailable, unavailable.Status);
    }

    [Fact]
    public async Task Preview_fails_closed_when_recording_clock_fails_after_valid_evidence()
    {
        var clock = new StubGovernedLoopSleepTimeProvider(_now) { ThrowOnCall = 2 };
        var fixture = new RepairFixture(clock: clock);

        var preview = await fixture.Service.PreviewAsync(new GovernedLoopCoordinatorRepairPreviewRequest("coordinator", "repair-operation"));

        Assert.Equal(GovernedLoopCoordinatorRepairPreviewStatus.Unavailable, preview.Status);
        Assert.Equal("coordinator-repair-clock-unavailable", preview.ReasonCode);
        Assert.Null(preview.Disposition);
        Assert.Empty(fixture.Repairs.Dispositions);
    }

    [Fact]
    public async Task Submit_fails_closed_when_repair_ledger_read_or_append_is_unavailable_or_malformed()
    {
        var unavailableRead = new RepairFixture();
        var unavailableReadPreview = await unavailableRead.Service.PreviewAsync(new GovernedLoopCoordinatorRepairPreviewRequest("coordinator", "repair-operation"));
        unavailableRead.Repairs.ReadException = new IOException("repair ledger unavailable");
        var unavailableReadResult = await unavailableRead.Service.SubmitAsync(new GovernedLoopCoordinatorRepairSubmitRequest(unavailableReadPreview.Disposition!));

        var malformedRead = new RepairFixture();
        var malformedReadPreview = await malformedRead.Service.PreviewAsync(new GovernedLoopCoordinatorRepairPreviewRequest("coordinator", "repair-operation"));
        malformedRead.Repairs.ForcedReadResult = new GovernedLoopCoordinatorRepairReadResult(GovernedLoopCoordinatorRepairReadStatus.Found);
        var malformedReadResult = await malformedRead.Service.SubmitAsync(new GovernedLoopCoordinatorRepairSubmitRequest(malformedReadPreview.Disposition!));

        var unavailableAppend = new RepairFixture();
        var unavailableAppendPreview = await unavailableAppend.Service.PreviewAsync(new GovernedLoopCoordinatorRepairPreviewRequest("coordinator", "repair-operation"));
        unavailableAppend.Repairs.AppendException = new IOException("repair ledger unavailable");
        var unavailableAppendResult = await unavailableAppend.Service.SubmitAsync(new GovernedLoopCoordinatorRepairSubmitRequest(unavailableAppendPreview.Disposition!));

        var malformedAppend = new RepairFixture();
        var malformedAppendPreview = await malformedAppend.Service.PreviewAsync(new GovernedLoopCoordinatorRepairPreviewRequest("coordinator", "repair-operation"));
        malformedAppend.Repairs.ForcedResult = new GovernedLoopCoordinatorRepairMutationResult(GovernedLoopCoordinatorRepairMutationStatus.Appended);
        var malformedAppendResult = await malformedAppend.Service.SubmitAsync(new GovernedLoopCoordinatorRepairSubmitRequest(malformedAppendPreview.Disposition!));

        Assert.Equal(GovernedLoopCoordinatorRepairSubmitStatus.Unavailable, unavailableReadResult.Status);
        Assert.Equal("coordinator-repair-ledger-unavailable", unavailableReadResult.ReasonCode);
        Assert.Equal(GovernedLoopCoordinatorRepairSubmitStatus.Corrupt, malformedReadResult.Status);
        Assert.Equal("coordinator-repair-ledger-corrupt", malformedReadResult.ReasonCode);
        Assert.Equal(GovernedLoopCoordinatorRepairSubmitStatus.Unavailable, unavailableAppendResult.Status);
        Assert.Equal("coordinator-repair-ledger-unavailable", unavailableAppendResult.ReasonCode);
        Assert.Equal(GovernedLoopCoordinatorRepairSubmitStatus.Corrupt, malformedAppendResult.Status);
        Assert.Equal("coordinator-repair-ledger-corrupt", malformedAppendResult.ReasonCode);
        Assert.Empty(unavailableRead.Repairs.Dispositions);
        Assert.Empty(malformedRead.Repairs.Dispositions);
        Assert.Empty(unavailableAppend.Repairs.Dispositions);
        Assert.Empty(malformedAppend.Repairs.Dispositions);
    }

    private static GovernedLoopCoordinatorSnapshot FixtureSnapshot()
    {
        var ownership = GovernedLoopSleepContractHash.Apply(new GovernedLoopCoordinatorOwnership(1, "coordinator", "owner", 2, _now.AddMinutes(-2), string.Empty));
        var lifecycle = GovernedLoopSleepContractHash.Apply(new GovernedLoopCoordinatorLifecycle(1, 3, ownership, GovernedLoopCoordinatorStatus.Failed, _now.AddMinutes(-1), _now.AddMinutes(-1), string.Empty));
        var heartbeat = GovernedLoopSleepContractHash.Apply(new GovernedLoopCoordinatorHeartbeat(1, 2, ownership, _now.AddMinutes(-2), _now.AddMinutes(-1), string.Empty));
        var failure = GovernedLoopSleepContractHash.Apply(new GovernedLoopCoordinatorFailure(1, 1, ownership, GovernedLoopCoordinatorFailureKind.CorruptState, "dependency-corrupt", _now.AddMinutes(-1), string.Empty));
        return new GovernedLoopCoordinatorSnapshot(ownership, lifecycle, heartbeat, failure.FailureSequence, failure.ContentHash);
    }

    private static GovernedLoopCoordinatorRepairReadiness Ready()
        => GovernedLoopSleepContractHash.Apply(new GovernedLoopCoordinatorRepairReadiness(1, _workspaceId, "coordinator", true, true, true, true, true, _now, string.Empty));

    private static string Hash(char value) => new(value, 64);

    private sealed class RepairFixture
    {
        internal RepairFixture(bool permitted = true, TimeProvider? clock = null)
        {
            Snapshot = FixtureSnapshot();
            Evidence = new EvidencePort(this);
            Repairs = new RepairPort();
            Dependencies = new DependencyPort { Readiness = Ready() };
            Authority = new AuthorityPort(permitted);
            Service = new GovernedLoopCoordinatorRepairService(
                _workspaceId,
                Authority,
                Evidence,
                Repairs,
                Dependencies,
                clock ?? new FixedClock(_now));
        }

        internal DependencyPort Dependencies { get; }

        internal AuthorityPort Authority { get; }

        internal EvidencePort Evidence { get; }

        internal RepairPort Repairs { get; }

        internal GovernedLoopCoordinatorRepairService Service { get; }

        internal GovernedLoopCoordinatorSnapshot Snapshot { get; set; }
    }

    private sealed class EvidencePort(RepairFixture fixture) : IGovernedLoopCoordinatorEvidencePort
    {
        internal Exception? Exception { get; set; }

        internal GovernedLoopCoordinatorReadResult? Result { get; set; }

        public Task<GovernedLoopCoordinatorReadResult?> ReadAsync(string coordinatorId, CancellationToken cancellationToken = default)
        {
            if (Exception is not null)
            {
                throw Exception;
            }

            return Task.FromResult<GovernedLoopCoordinatorReadResult?>(Result ?? new GovernedLoopCoordinatorReadResult(
                GovernedLoopCoordinatorReadStatus.Found,
                fixture.Snapshot));
        }

        public Task<GovernedLoopCoordinatorAcquisitionResult?> TryAcquireAsync(GovernedLoopCoordinatorAcquisitionRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult<GovernedLoopCoordinatorAcquisitionResult?>(new GovernedLoopCoordinatorAcquisitionResult(GovernedLoopCoordinatorAcquisitionStatus.Unavailable));

        public Task<GovernedLoopCoordinatorHeartbeatMutationResult?> RenewHeartbeatAsync(GovernedLoopCoordinatorHeartbeatMutationRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult<GovernedLoopCoordinatorHeartbeatMutationResult?>(new GovernedLoopCoordinatorHeartbeatMutationResult(GovernedLoopCoordinatorHeartbeatMutationStatus.Unavailable));

        public Task<GovernedLoopCoordinatorLifecycleMutationResult?> AppendLifecycleAsync(GovernedLoopCoordinatorLifecycleMutationRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult<GovernedLoopCoordinatorLifecycleMutationResult?>(new GovernedLoopCoordinatorLifecycleMutationResult(GovernedLoopCoordinatorLifecycleMutationStatus.Unavailable));

        public Task<GovernedLoopCoordinatorFailureMutationResult?> AppendFailureAsync(GovernedLoopCoordinatorFailureMutationRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult<GovernedLoopCoordinatorFailureMutationResult?>(new GovernedLoopCoordinatorFailureMutationResult(GovernedLoopCoordinatorFailureMutationStatus.Unavailable));
    }

    private sealed class RepairPort : IGovernedLoopCoordinatorRepairPort
    {
        internal List<GovernedLoopCoordinatorRepairDisposition> Dispositions { get; } = [];

        internal Exception? AppendException { get; set; }

        internal GovernedLoopCoordinatorRepairMutationResult? ForcedResult { get; set; }

        internal Exception? ReadException { get; set; }

        internal GovernedLoopCoordinatorRepairReadResult? ForcedReadResult { get; set; }

        public Task<GovernedLoopCoordinatorRepairReadResult?> ReadAsync(string coordinatorId, string failedOwnershipHash, CancellationToken cancellationToken = default)
        {
            if (ReadException is not null)
            {
                throw ReadException;
            }

            if (ForcedReadResult is not null)
            {
                return Task.FromResult<GovernedLoopCoordinatorRepairReadResult?>(ForcedReadResult);
            }

            var disposition = Dispositions.SingleOrDefault(item => string.Equals(
                item.FailedOwnership.ContentHash,
                failedOwnershipHash,
                StringComparison.Ordinal));
            return Task.FromResult<GovernedLoopCoordinatorRepairReadResult?>(disposition is null
                ? new GovernedLoopCoordinatorRepairReadResult(GovernedLoopCoordinatorRepairReadStatus.NotFound)
                : new GovernedLoopCoordinatorRepairReadResult(GovernedLoopCoordinatorRepairReadStatus.Found, disposition));
        }

        public Task<GovernedLoopCoordinatorRepairMutationResult?> AppendAsync(GovernedLoopCoordinatorRepairDisposition disposition, CancellationToken cancellationToken = default)
        {
            if (AppendException is not null)
            {
                throw AppendException;
            }

            if (ForcedResult is not null)
            {
                return Task.FromResult<GovernedLoopCoordinatorRepairMutationResult?>(ForcedResult);
            }

            var prior = Dispositions.SingleOrDefault(item => item.OperationId == disposition.OperationId);
            if (prior is not null)
            {
                return Task.FromResult<GovernedLoopCoordinatorRepairMutationResult?>(new GovernedLoopCoordinatorRepairMutationResult(
                    prior == disposition ? GovernedLoopCoordinatorRepairMutationStatus.Duplicate : GovernedLoopCoordinatorRepairMutationStatus.Conflict,
                    prior == disposition ? prior : null));
            }

            Dispositions.Add(disposition);
            return Task.FromResult<GovernedLoopCoordinatorRepairMutationResult?>(new GovernedLoopCoordinatorRepairMutationResult(GovernedLoopCoordinatorRepairMutationStatus.Appended, disposition));
        }

        public Task<GovernedLoopCoordinatorAcquisitionResult?> TryAcquireAfterRepairAsync(GovernedLoopCoordinatorRepairAcquisitionRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult<GovernedLoopCoordinatorAcquisitionResult?>(new GovernedLoopCoordinatorAcquisitionResult(GovernedLoopCoordinatorAcquisitionStatus.Unavailable));
    }

    private sealed class DependencyPort : IGovernedLoopCoordinatorRepairDependencyPort
    {
        internal Exception? Exception { get; set; }

        internal GovernedLoopCoordinatorRepairReadiness Readiness { get; set; } = null!;

        public Task<GovernedLoopCoordinatorRepairReadiness?> ReadAsync(string workspaceId, string coordinatorId, CancellationToken cancellationToken = default)
        {
            if (Exception is not null)
            {
                throw Exception;
            }

            return Task.FromResult<GovernedLoopCoordinatorRepairReadiness?>(Readiness);
        }
    }

    private sealed class AuthorityPort(bool permitted) : IGovernedLoopOperationalControlAuthorityPort
    {
        internal string ActorId { get; set; } = "operator";

        internal Exception? Exception { get; set; }

        public Task<GovernedLoopOperationalControlAuthority?> ReadCurrentAsync(CancellationToken cancellationToken = default)
        {
            if (Exception is not null)
            {
                throw Exception;
            }

            return Task.FromResult<GovernedLoopOperationalControlAuthority?>(Authority());
        }

        public Task<GovernedLoopOperationalControlAuthority?> ReadAsync(GovernedLoopOperationalControlRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult<GovernedLoopOperationalControlAuthority?>(Authority());

        private GovernedLoopOperationalControlAuthority Authority()
            => new(1, _workspaceId, ActorId, "surface", _now, Hash('a'), permitted, "authorized");
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
