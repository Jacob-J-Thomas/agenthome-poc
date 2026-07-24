using EmbodySense.Core.Application.Governance.Audit;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.ReceiptRetention;
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
    public async Task Outcome_audit_failure_reports_a_committed_warning_and_a_later_retry_completes_it()
    {
        var store = new FakeStore(Operation(CustomLoopInvocationReceiptRetentionOperationState.Reserved));
        var audit = new FakeAuditLog { FailingAction = AuditSchema.Actions.LoopInvocationReceiptRetentionOutcome };
        var service = Service(store, audit);

        var warning = await service.PruneForCapacityAsync("embodysense.web", "web");
        audit.FailingAction = null;
        var replay = await service.PruneForCapacityAsync("embodysense.web", "web");

        Assert.Equal(CustomLoopInvocationReceiptRetentionStatus.CommittedWithAuditWarning, warning.Status);
        Assert.True(warning.AllowsReceiptWrite);
        Assert.Equal(CustomLoopInvocationReceiptRetentionStatus.Replayed, replay.Status);
        Assert.Equal(CustomLoopInvocationReceiptRetentionOperationState.OutcomeAuditRecorded, store.Current!.State);
        Assert.Equal(2, audit.Events.Count(item => item.Action == AuditSchema.Actions.LoopInvocationReceiptRetentionOutcome));
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

    private static CustomLoopInvocationReceiptRetentionService Service(FakeStore store, FakeAuditLog audit)
    {
        return new CustomLoopInvocationReceiptRetentionService(store, audit, new FixedTimeProvider(Now));
    }

    private static CustomLoopInvocationReceiptRetentionOperation Operation(CustomLoopInvocationReceiptRetentionOperationState state)
    {
        var candidates = new[]
        {
            new CustomLoopInvocationReceiptRetentionCandidate("invoke-old-a", Now.AddDays(-31), new string('a', 64), 100),
            new CustomLoopInvocationReceiptRetentionCandidate("invoke-old-b", Now.AddDays(-30), new string('b', 64), 200)
        };
        var committed = state is CustomLoopInvocationReceiptRetentionOperationState.OutcomeCommitted or CustomLoopInvocationReceiptRetentionOperationState.OutcomeAuditRecorded;
        return new CustomLoopInvocationReceiptRetentionOperation(
            CustomLoopInvocationReceiptRetentionOperation.CurrentSchemaVersion,
            "receipt-retention-existing",
            "embodysense.web",
            "web",
            Now,
            Now.AddDays(-30),
            Now,
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

        public string? FailingAction { get; set; }

        public Task AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            Events.Add(auditEvent);
            if (string.Equals(auditEvent.Action, FailingAction, StringComparison.Ordinal))
            {
                throw new IOException("audit unavailable");
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AuditEvent>> ReadTailAsync(int limit, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AuditEvent>>(Events.TakeLast(limit).ToArray());
    }

    private sealed class FakeStore(CustomLoopInvocationReceiptRetentionOperation current) : ICustomLoopInvocationOperationStore
    {
        public CustomLoopInvocationReceiptRetentionOperation? Current { get; private set; } = current;

        public CustomLoopInvocationReceiptRetentionReservationStatus? ReservationStatus { get; init; }

        public Exception? ReserveException { get; init; }

        public int CommitCalls { get; private set; }

        public Task<CustomLoopInvocationReceiptRetentionReservationResult> ReserveCompletedReceiptRetentionAsync(CustomLoopInvocationReceiptRetentionRequest request, CancellationToken cancellationToken = default)
        {
            if (ReserveException is not null)
            {
                throw ReserveException;
            }

            var status = ReservationStatus ?? Current!.State switch
            {
                CustomLoopInvocationReceiptRetentionOperationState.Reserved => CustomLoopInvocationReceiptRetentionReservationStatus.Reserved,
                CustomLoopInvocationReceiptRetentionOperationState.IntentAuditRecorded => CustomLoopInvocationReceiptRetentionReservationStatus.ReadyToCommit,
                CustomLoopInvocationReceiptRetentionOperationState.OutcomeCommitted => CustomLoopInvocationReceiptRetentionReservationStatus.OutcomeCommitted,
                CustomLoopInvocationReceiptRetentionOperationState.OutcomeAuditRecorded => CustomLoopInvocationReceiptRetentionReservationStatus.OutcomeCommitted,
                _ => throw new InvalidOperationException()
            };
            return Task.FromResult(new CustomLoopInvocationReceiptRetentionReservationResult(status, status == CustomLoopInvocationReceiptRetentionReservationStatus.NothingEligible ? null : Current));
        }

        public Task<CustomLoopInvocationReceiptRetentionOperation> MarkReceiptRetentionIntentAuditedAsync(string operationId, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default)
        {
            Current = Current! with { State = CustomLoopInvocationReceiptRetentionOperationState.IntentAuditRecorded, UpdatedAtUtc = updatedAtUtc };
            return Task.FromResult(Current);
        }

        public Task<CustomLoopInvocationReceiptRetentionOperation> CommitCompletedReceiptRetentionAsync(string operationId, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default)
        {
            CommitCalls++;
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
            Current = Current! with { State = CustomLoopInvocationReceiptRetentionOperationState.OutcomeAuditRecorded, UpdatedAtUtc = updatedAtUtc };
            return Task.FromResult(Current);
        }

        public Task<CustomLoopInvocationOperationStoreResult> BeginAsync(CustomLoopInvocationOperation operation, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CustomLoopInvocationOperationStoreResult> BindAsync(CustomLoopInvocationOperation operation, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CustomLoopInvocationOperation?> GetAsync(string operationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CustomLoopInvocationOperationStoreResult> CompleteAsync(CustomLoopInvocationOperation operation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
