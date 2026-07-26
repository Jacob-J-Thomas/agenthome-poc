using EmbodySense.Core.Application.Governance.Audit;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.ReceiptRetention;
using EmbodySense.Core.Application.Loops.ReceiptRetention.Models;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.Governance.Audit.Models;

namespace EmbodySense.Core.Application.Tests.Loops.ReceiptRetention;

public sealed class CustomLoopInvocationReceiptRetentionServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Cleanup_records_intent_and_outcome_around_the_exact_committed_batch()
    {
        var store = new FakeStore(Operation(CustomLoopInvocationReceiptRetentionOperationState.Reserved));
        var audit = new FakeAuditLog();
        var result = await Service(store, audit).PruneForCapacityAsync("embodysense.web", "web");

        Assert.Equal(CustomLoopInvocationReceiptRetentionStatus.Pruned, result.Status);
        Assert.True(result.AllowsReceiptWrite);
        Assert.Equal(2, result.DeletedReceiptCount);
        Assert.Equal(300, result.DeletedReceiptUtf8Bytes);
        Assert.Equal([AuditSchema.Actions.LoopInvocationReceiptRetentionIntent, AuditSchema.Actions.LoopInvocationReceiptRetentionOutcome], audit.Events.Select(item => item.Action));
        Assert.Equal(CustomLoopInvocationReceiptRetentionOperationState.OutcomeAuditRecorded, store.Current!.State);
        Assert.Equal(1, store.CommitCalls);
        Assert.NotEmpty(store.MutationTokens);
        Assert.All(store.MutationTokens, token => Assert.Equal(audit.AppendTokens[0], token));
        Assert.All(audit.AppendTokens, token => Assert.Equal(audit.AppendTokens[0], token));
    }

    [Fact]
    public async Task Intent_audit_failure_never_advances_or_deletes_the_reserved_batch()
    {
        var store = new FakeStore(Operation(CustomLoopInvocationReceiptRetentionOperationState.Reserved));
        var audit = new FakeAuditLog { FailingAction = AuditSchema.Actions.LoopInvocationReceiptRetentionIntent };

        var result = await Service(store, audit).PruneForCapacityAsync("embodysense.web", "web");

        Assert.Equal(CustomLoopInvocationReceiptRetentionStatus.AuditUnavailable, result.Status);
        Assert.False(result.AllowsReceiptWrite);
        Assert.Equal(CustomLoopInvocationReceiptRetentionOperationState.Reserved, store.Current!.State);
        Assert.Equal(0, store.CommitCalls);
    }

    [Fact]
    public async Task Outcome_audit_failure_reports_a_committed_warning_without_duplicating_the_attempt()
    {
        var store = new FakeStore(Operation(CustomLoopInvocationReceiptRetentionOperationState.Reserved));
        var audit = new FakeAuditLog { FailingAction = AuditSchema.Actions.LoopInvocationReceiptRetentionOutcome };
        var service = Service(store, audit);

        var warning = await service.PruneForCapacityAsync("embodysense.web", "web");
        audit.FailingAction = null;
        var replay = await service.PruneForCapacityAsync("embodysense.web", "web");

        Assert.Equal(CustomLoopInvocationReceiptRetentionStatus.CommittedWithAuditWarning, warning.Status);
        Assert.True(warning.AllowsReceiptWrite);
        Assert.Equal(CustomLoopInvocationReceiptRetentionStatus.CommittedWithAuditWarning, replay.Status);
        Assert.Equal(CustomLoopInvocationReceiptRetentionOperationState.CommittedWithAuditWarning, store.Current!.State);
        Assert.Single(audit.Events, item => item.Action == AuditSchema.Actions.LoopInvocationReceiptRetentionOutcome);
    }

    [Fact]
    public async Task Changed_candidate_is_conflict_audited_and_reselected_before_cleanup_continues()
    {
        var store = new FakeStore(Operation(CustomLoopInvocationReceiptRetentionOperationState.Reserved)) { AbandonFirstCommit = true };
        var audit = new FakeAuditLog();

        var result = await Service(store, audit).PruneForCapacityAsync("embodysense.web", "web");

        Assert.Equal(CustomLoopInvocationReceiptRetentionStatus.Pruned, result.Status);
        Assert.Equal(2, store.CommitCalls);
        Assert.Equal(
            [
                (AuditSchema.Actions.LoopInvocationReceiptRetentionIntent, AuditSchema.Outcomes.Requested),
                (AuditSchema.Actions.LoopInvocationReceiptRetentionOutcome, AuditSchema.Outcomes.Conflict),
                (AuditSchema.Actions.LoopInvocationReceiptRetentionIntent, AuditSchema.Outcomes.Requested),
                (AuditSchema.Actions.LoopInvocationReceiptRetentionOutcome, AuditSchema.Outcomes.Succeeded)
            ],
            audit.Events.Select(item => (item.Action, item.Outcome)));
    }

    [Theory]
    [InlineData(CustomLoopInvocationReceiptRetentionReservationStatus.NothingEligible, CustomLoopInvocationReceiptRetentionStatus.NothingEligible)]
    [InlineData(CustomLoopInvocationReceiptRetentionReservationStatus.OperationInProgress, CustomLoopInvocationReceiptRetentionStatus.OperationInProgress)]
    public async Task Cleanup_preserves_quota_and_owner_boundaries(CustomLoopInvocationReceiptRetentionReservationStatus reservationStatus, CustomLoopInvocationReceiptRetentionStatus expected)
    {
        var store = new FakeStore(Operation(CustomLoopInvocationReceiptRetentionOperationState.Reserved)) { ReservationStatus = reservationStatus };
        var result = await Service(store, new FakeAuditLog()).PruneForCapacityAsync("embodysense.web", "web");

        Assert.Equal(expected, result.Status);
        Assert.False(result.AllowsReceiptWrite);
        Assert.Equal(0, store.CommitCalls);
    }

    [Fact]
    public async Task Invalid_actor_or_persistence_failure_is_visible_and_never_audited()
    {
        var audit = new FakeAuditLog();
        var store = new FakeStore(Operation(CustomLoopInvocationReceiptRetentionOperationState.Reserved)) { ReserveException = new FormatException("corrupt") };

        var invalid = await Service(store, audit).PruneForCapacityAsync("unsafe\nactor", "web");
        var failed = await Service(store, audit).PruneForCapacityAsync("embodysense.web", "web");

        Assert.Equal(CustomLoopInvocationReceiptRetentionStatus.Invalid, invalid.Status);
        Assert.Equal(CustomLoopInvocationReceiptRetentionStatus.Invalid, failed.Status);
        Assert.Empty(audit.Events);
    }

    [Fact]
    public async Task Owner_window_is_capped_from_the_durable_reservation_timestamp()
    {
        var ownershipStartedAtUtc = Now - CustomLoopInvocationReceiptRetentionPolicy.OperationOwnershipWindow;
        var store = new FakeStore(Operation(CustomLoopInvocationReceiptRetentionOperationState.Reserved, ownershipStartedAtUtc));
        var audit = new FakeAuditLog { BlockingAction = AuditSchema.Actions.LoopInvocationReceiptRetentionIntent };

        var result = await Service(store, audit).PruneForCapacityAsync("embodysense.web", "web");

        Assert.Equal(CustomLoopInvocationReceiptRetentionStatus.AuditUnavailable, result.Status);
        Assert.Equal(0, store.CommitCalls);
        Assert.True(Assert.Single(audit.AppendTokens).IsCancellationRequested);
    }

    private static CustomLoopInvocationReceiptRetentionService Service(FakeStore store, FakeAuditLog audit)
    {
        return new CustomLoopInvocationReceiptRetentionService(store, audit, new FixedTimeProvider(Now));
    }

    private static CustomLoopInvocationReceiptRetentionOperation Operation(CustomLoopInvocationReceiptRetentionOperationState state, DateTimeOffset? ownershipStartedAtUtc = null)
    {
        var startedAtUtc = ownershipStartedAtUtc ?? Now;
        var candidates = new[]
        {
            new CustomLoopInvocationReceiptRetentionCandidate("invoke-old-a", Now.AddDays(-31), new string('a', 64), 100),
            new CustomLoopInvocationReceiptRetentionCandidate("invoke-old-b", Now.AddDays(-30), new string('b', 64), 200)
        };
        var committed = state is CustomLoopInvocationReceiptRetentionOperationState.OutcomeCommitted
            or CustomLoopInvocationReceiptRetentionOperationState.OutcomeAuditStarted
            or CustomLoopInvocationReceiptRetentionOperationState.OutcomeAuditRecorded
            or CustomLoopInvocationReceiptRetentionOperationState.CommittedWithAuditWarning;
        return new CustomLoopInvocationReceiptRetentionOperation(
            CustomLoopInvocationReceiptRetentionOperation.CurrentSchemaVersion,
            "receipt-retention-existing",
            "embodysense.web",
            "web",
            startedAtUtc,
            startedAtUtc - CustomLoopInvocationReceiptRetentionPolicy.MinimumReplayDuration,
            startedAtUtc,
            startedAtUtc,
            candidates,
            state,
            committed ? candidates.Length : 0,
            committed ? candidates.Sum(candidate => candidate.ArtifactUtf8Bytes) : 0);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeAuditLog : IAuditLog
    {
        public List<AuditEvent> Events { get; } = [];

        public List<CancellationToken> AppendTokens { get; } = [];

        public string? FailingAction { get; set; }

        public string? BlockingAction { get; init; }

        public async Task AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            Events.Add(auditEvent);
            AppendTokens.Add(cancellationToken);
            if (string.Equals(auditEvent.Action, BlockingAction, StringComparison.Ordinal))
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            if (string.Equals(auditEvent.Action, FailingAction, StringComparison.Ordinal))
            {
                throw new IOException("audit unavailable");
            }
        }

        public Task<IReadOnlyList<AuditEvent>> ReadTailAsync(int limit, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AuditEvent>>(Events.TakeLast(limit).ToArray());
    }

    private sealed class FakeStore(CustomLoopInvocationReceiptRetentionOperation current) : ICustomLoopInvocationOperationStore
    {
        public CustomLoopInvocationReceiptRetentionOperation? Current { get; private set; } = current;

        public CustomLoopInvocationReceiptRetentionReservationStatus? ReservationStatus { get; init; }

        public Exception? ReserveException { get; init; }

        public bool AbandonFirstCommit { get; init; }

        public int CommitCalls { get; private set; }

        public List<CancellationToken> MutationTokens { get; } = [];

        public Task<CustomLoopInvocationReceiptRetentionReservationResult> ReserveCompletedReceiptRetentionAsync(CustomLoopInvocationReceiptRetentionRequest request, CancellationToken cancellationToken = default)
        {
            if (ReserveException is not null)
            {
                throw ReserveException;
            }

            if (Current!.State == CustomLoopInvocationReceiptRetentionOperationState.AbandonedCandidateChanged)
            {
                Current = Operation(CustomLoopInvocationReceiptRetentionOperationState.Reserved) with
                {
                    OperationId = request.OperationId,
                    RequestedAtUtc = request.RequestedAtUtc,
                    ReplayCutoffUtc = request.ReplayCutoffUtc,
                    OwnershipStartedAtUtc = request.RequestedAtUtc,
                    UpdatedAtUtc = request.RequestedAtUtc
                };
            }

            var status = ReservationStatus ?? Current!.State switch
            {
                CustomLoopInvocationReceiptRetentionOperationState.Reserved => CustomLoopInvocationReceiptRetentionReservationStatus.Reserved,
                CustomLoopInvocationReceiptRetentionOperationState.IntentAuditRecorded => CustomLoopInvocationReceiptRetentionReservationStatus.ReadyToCommit,
                CustomLoopInvocationReceiptRetentionOperationState.OutcomeCommitted => CustomLoopInvocationReceiptRetentionReservationStatus.OutcomeCommitted,
                CustomLoopInvocationReceiptRetentionOperationState.OutcomeAuditStarted => CustomLoopInvocationReceiptRetentionReservationStatus.OutcomeCommitted,
                CustomLoopInvocationReceiptRetentionOperationState.OutcomeAuditRecorded => CustomLoopInvocationReceiptRetentionReservationStatus.OutcomeCommitted,
                CustomLoopInvocationReceiptRetentionOperationState.CommittedWithAuditWarning => CustomLoopInvocationReceiptRetentionReservationStatus.OutcomeCommitted,
                _ => throw new InvalidOperationException()
            };
            return Task.FromResult(new CustomLoopInvocationReceiptRetentionReservationResult(status, status == CustomLoopInvocationReceiptRetentionReservationStatus.NothingEligible ? null : Current));
        }

        public Task<CustomLoopInvocationReceiptRetentionOperation> MarkReceiptRetentionIntentAuditedAsync(string operationId, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default)
        {
            MutationTokens.Add(cancellationToken);
            Current = Current! with { State = CustomLoopInvocationReceiptRetentionOperationState.IntentAuditRecorded, UpdatedAtUtc = updatedAtUtc };
            return Task.FromResult(Current);
        }

        public Task<CustomLoopInvocationReceiptRetentionOperation> CommitCompletedReceiptRetentionAsync(string operationId, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default)
        {
            MutationTokens.Add(cancellationToken);
            CommitCalls++;
            if (AbandonFirstCommit && CommitCalls == 1)
            {
                Current = Current! with
                {
                    State = CustomLoopInvocationReceiptRetentionOperationState.AbandonedCandidateChanged,
                    UpdatedAtUtc = updatedAtUtc,
                    DeletedReceiptCount = 0,
                    DeletedReceiptUtf8Bytes = 0
                };
                return Task.FromResult(Current);
            }

            Current = Current! with
            {
                State = CustomLoopInvocationReceiptRetentionOperationState.OutcomeCommitted,
                UpdatedAtUtc = updatedAtUtc,
                DeletedReceiptCount = Current.Candidates.Length,
                DeletedReceiptUtf8Bytes = Current.Candidates.Sum(candidate => candidate.ArtifactUtf8Bytes)
            };
            return Task.FromResult(Current);
        }

        public Task<CustomLoopInvocationReceiptRetentionOperation> MarkReceiptRetentionOutcomeAuditedAsync(string operationId, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default)
        {
            MutationTokens.Add(cancellationToken);
            Current = Current! with { State = CustomLoopInvocationReceiptRetentionOperationState.OutcomeAuditRecorded, UpdatedAtUtc = updatedAtUtc };
            return Task.FromResult(Current);
        }

        public Task<CustomLoopInvocationReceiptRetentionOperation> MarkReceiptRetentionOutcomeAuditStartedAsync(string operationId, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default)
        {
            MutationTokens.Add(cancellationToken);
            Current = Current! with { State = CustomLoopInvocationReceiptRetentionOperationState.OutcomeAuditStarted, UpdatedAtUtc = updatedAtUtc };
            return Task.FromResult(Current);
        }

        public Task<CustomLoopInvocationReceiptRetentionOperation> MarkReceiptRetentionOutcomeAuditWarningAsync(string operationId, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default)
        {
            MutationTokens.Add(cancellationToken);
            Current = Current! with { State = CustomLoopInvocationReceiptRetentionOperationState.CommittedWithAuditWarning, UpdatedAtUtc = updatedAtUtc };
            return Task.FromResult(Current);
        }

        public Task<CustomLoopInvocationOperationStoreResult> BeginAsync(CustomLoopInvocationOperation operation, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CustomLoopInvocationOperationStoreResult> BindAsync(CustomLoopInvocationOperation operation, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CustomLoopInvocationOperation?> GetAsync(string operationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CustomLoopInvocationOperationStoreResult> CompleteAsync(CustomLoopInvocationOperation operation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
