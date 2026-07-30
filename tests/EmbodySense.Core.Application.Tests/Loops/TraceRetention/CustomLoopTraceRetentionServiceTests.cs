using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Loops.TraceRetention.Models;
using EmbodySense.Core.Application.Governance.Audit;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.TraceRetention;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Tests.Loops.TraceRetention;

public sealed class CustomLoopTraceRetentionServiceTests
{
    private static readonly DateTimeOffset _timestamp = DateTimeOffset.Parse("2026-07-16T12:00:00+00:00");
    private const string TraceHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task Delete_records_intent_before_mutation_and_outcome_before_complete_marker()
    {
        var request = Request();
        var tombstone = Tombstone(request, CustomLoopTraceDeletionIntegrity.PendingOutcomeAudit);
        var store = new RecordingStore(Inspection(), new CustomLoopTraceDeletionStoreResult(CustomLoopTraceDeletionStoreStatus.Deleted, tombstone, CustomLoopTraceDeletionIntegrity.PendingOutcomeAudit));
        var audit = new RecordingAuditLog();
        var service = new CustomLoopTraceRetentionService(store, audit, new FixedTimeProvider(_timestamp.AddMinutes(3)));

        var result = await service.DeleteAsync(request);

        Assert.Equal(CustomLoopTraceDeletionStatus.Deleted, result.Status);
        Assert.Equal(CustomLoopTraceDeletionIntegrity.Complete, result.Tombstone!.OutcomeIntegrity);
        Assert.Equal(new[] { AuditSchema.Actions.LoopTraceDeletionIntent, AuditSchema.Actions.LoopTraceDeletionOutcome }, audit.Events.Select(item => item.Action));
        Assert.Equal(new[] { CustomLoopTraceDeletionIntegrity.OutcomeAuditStarted, CustomLoopTraceDeletionIntegrity.Complete }, store.MarkedIntegrities);
        Assert.Equal(1, store.DeleteCalls);
        Assert.All(audit.Events, item =>
        {
            Assert.Equal(request.Actor, item.Actor);
            Assert.Equal(request.RunId, item.Target);
            Assert.Equal(request.OperationId, item.Metadata["operation_id"]);
            Assert.DoesNotContain("sensitive", item.Detail, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task Intent_audit_failure_preserves_content_and_durably_audits_the_terminal_rejection()
    {
        var store = new RecordingStore(Inspection(), null);
        var audit = new RecordingAuditLog(failOnAttempt: 1);

        var result = await new CustomLoopTraceRetentionService(store, audit).DeleteAsync(Request());

        Assert.Equal(CustomLoopTraceDeletionStatus.AuditUnavailable, result.Status);
        Assert.True(result.IsOutcomeCommitted);
        Assert.Equal(0, store.DeleteCalls);
        Assert.Equal(CustomLoopTraceDeletionStoreStatus.AuditUnavailable, store.Operation!.Outcome);
        Assert.Equal(CustomLoopTraceDeletionIntegrity.Complete, store.Operation.Integrity);
        Assert.Equal(new[] { AuditSchema.Actions.LoopTraceDeletionOutcome }, audit.Events.Select(item => item.Action));
        Assert.Equal(new[] { CustomLoopTraceDeletionIntegrity.OutcomeAuditStarted, CustomLoopTraceDeletionIntegrity.Complete }, store.MarkedIntegrities);
    }

    [Fact]
    public async Task Intent_audit_failure_with_an_ambiguous_rejection_receipt_preserves_the_operation_for_recovery()
    {
        var store = new RecordingStore(Inspection(), null) { CommitAuditFailureException = new IOException("receipt response lost") };
        var audit = new RecordingAuditLog(failOnAttempt: 1);

        var result = await new CustomLoopTraceRetentionService(store, audit).DeleteAsync(Request());

        Assert.Equal(CustomLoopTraceDeletionStatus.AuditUnavailable, result.Status);
        Assert.False(result.IsOutcomeCommitted);
        Assert.Equal(CustomLoopTraceDeletionOperationState.PendingMutation, store.Operation!.State);
        Assert.Equal(0, store.DeleteCalls);
        Assert.Contains("requires recovery", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delayed_reservation_response_cannot_extend_the_owner_past_its_durable_deadline()
    {
        var store = new RecordingStore(Inspection(), null);
        var audit = new RecordingAuditLog();

        var result = await new CustomLoopTraceRetentionService(store, audit, new SequencedTimeProvider(_timestamp.AddMinutes(3), _timestamp.AddMinutes(3).AddSeconds(31))).DeleteAsync(Request());

        Assert.Equal(CustomLoopTraceDeletionStatus.AuditUnavailable, result.Status);
        Assert.True(result.IsOutcomeCommitted);
        Assert.Equal(0, store.DeleteCalls);
        Assert.Equal(CustomLoopTraceDeletionStoreStatus.AuditUnavailable, store.Operation!.Outcome);
    }

    [Fact]
    public async Task Overlapping_request_observes_the_reserved_operation_without_duplicate_intent_or_mutation()
    {
        var request = Request();
        var tombstone = Tombstone(request, CustomLoopTraceDeletionIntegrity.PendingOutcomeAudit);
        var store = new RecordingStore(Inspection(), new CustomLoopTraceDeletionStoreResult(CustomLoopTraceDeletionStoreStatus.Deleted, tombstone, CustomLoopTraceDeletionIntegrity.PendingOutcomeAudit));
        var audit = new BlockingIntentAuditLog();
        var service = new CustomLoopTraceRetentionService(store, audit, new FixedTimeProvider(_timestamp.AddMinutes(3)));
        var firstTask = service.DeleteAsync(request);
        await audit.IntentStarted.WaitAsync(TimeSpan.FromSeconds(2));

        var overlapping = await service.DeleteAsync(request);
        audit.ReleaseIntent();
        var first = await firstTask;

        Assert.Equal(CustomLoopTraceDeletionStatus.OperationInProgress, overlapping.Status);
        Assert.Equal(CustomLoopTraceDeletionStatus.Deleted, first.Status);
        Assert.Equal(1, audit.Events.Count(item => item.Action == AuditSchema.Actions.LoopTraceDeletionIntent));
        Assert.Equal(1, audit.Events.Count(item => item.Action == AuditSchema.Actions.LoopTraceDeletionOutcome));
        Assert.Equal(1, store.ReserveCalls);
        Assert.Equal(1, store.DeleteCalls);
        Assert.True(audit.IntentCancellationToken.CanBeCanceled);
        Assert.Equal(audit.IntentCancellationToken, store.DeletionCancellationToken);
    }

    [Fact]
    public async Task Stale_pending_reservation_fails_closed_with_one_durable_outcome_and_replays_without_more_audit()
    {
        var request = Request();
        var pending = PendingOperation(request, _timestamp.AddMinutes(3));
        var store = new RecordingStore(null, null) { Operation = pending };
        var audit = new RecordingAuditLog();
        var service = new CustomLoopTraceRetentionService(store, audit, new FixedTimeProvider(_timestamp.AddMinutes(4)));

        var recovered = await service.DeleteAsync(request);
        var attemptsAfterRecovery = audit.Attempts;
        var replay = await service.DeleteAsync(request);

        Assert.Equal(CustomLoopTraceDeletionStatus.AuditUnavailable, recovered.Status);
        Assert.Equal(CustomLoopTraceDeletionStatus.AuditUnavailable, replay.Status);
        Assert.Equal(CustomLoopTraceDeletionOperationState.OutcomeCommitted, store.Operation!.State);
        Assert.Equal(CustomLoopTraceDeletionStoreStatus.AuditUnavailable, store.Operation.Outcome);
        Assert.Equal(CustomLoopTraceDeletionIntegrity.Complete, store.Operation.Integrity);
        Assert.Equal(new[] { AuditSchema.Actions.LoopTraceDeletionOutcome }, audit.Events.Select(item => item.Action));
        Assert.Equal(attemptsAfterRecovery, audit.Attempts);
        Assert.Equal(0, store.DeleteCalls);
    }

    [Fact]
    public async Task Lost_reservation_response_is_reconciled_without_claiming_or_repeating_the_intent()
    {
        var store = new RecordingStore(Inspection(), null)
        {
            ReserveException = new IOException("reservation response lost"),
            ReserveBeforeThrow = true
        };
        var audit = new RecordingAuditLog();
        var request = Request();

        var ambiguous = await new CustomLoopTraceRetentionService(store, audit, new FixedTimeProvider(_timestamp.AddMinutes(3))).DeleteAsync(request);
        store.ReserveException = null;
        var recovered = await new CustomLoopTraceRetentionService(store, audit, new FixedTimeProvider(_timestamp.AddMinutes(4))).DeleteAsync(request);

        Assert.Equal(CustomLoopTraceDeletionStatus.OperationInProgress, ambiguous.Status);
        Assert.Equal(CustomLoopTraceDeletionStatus.AuditUnavailable, recovered.Status);
        Assert.Equal(new[] { AuditSchema.Actions.LoopTraceDeletionOutcome }, audit.Events.Select(item => item.Action));
        Assert.Equal(0, store.DeleteCalls);
    }

    [Fact]
    public async Task Stale_pending_reservation_reconciles_a_matching_tombstone_before_failing_closed()
    {
        var request = Request();
        var tombstone = Tombstone(request, CustomLoopTraceDeletionIntegrity.PendingOutcomeAudit);
        var deletedInspection = Inspection() with { Kind = CustomLoopTraceArtifactKind.Tombstone, Tombstone = tombstone };
        var stored = new CustomLoopTraceDeletionStoreResult(CustomLoopTraceDeletionStoreStatus.Deleted, tombstone, CustomLoopTraceDeletionIntegrity.PendingOutcomeAudit);
        var store = new RecordingStore(deletedInspection, stored)
        {
            Operation = PendingOperation(request, _timestamp.AddMinutes(3))
        };
        var audit = new RecordingAuditLog();

        var result = await new CustomLoopTraceRetentionService(store, audit, new FixedTimeProvider(_timestamp.AddMinutes(4))).DeleteAsync(request);

        Assert.Equal(CustomLoopTraceDeletionStatus.Replayed, result.Status);
        Assert.Equal(CustomLoopTraceDeletionIntegrity.Complete, result.Tombstone!.OutcomeIntegrity);
        Assert.Equal(1, store.DeleteCalls);
        Assert.Equal(new[] { AuditSchema.Actions.LoopTraceDeletionOutcome }, audit.Events.Select(item => item.Action));
    }

    [Fact]
    public async Task Outcome_audit_failure_is_durably_marked_and_replay_does_not_repeat_audit_or_mutation()
    {
        var request = Request();
        var tombstone = Tombstone(request, CustomLoopTraceDeletionIntegrity.PendingOutcomeAudit);
        var store = new RecordingStore(Inspection(), new CustomLoopTraceDeletionStoreResult(CustomLoopTraceDeletionStoreStatus.Deleted, tombstone, CustomLoopTraceDeletionIntegrity.PendingOutcomeAudit));
        var audit = new RecordingAuditLog(failOnAttempt: 2);
        var service = new CustomLoopTraceRetentionService(store, audit, new FixedTimeProvider(_timestamp.AddMinutes(3)));

        var first = await service.DeleteAsync(request);
        var attemptsAfterFirst = audit.Attempts;
        var replay = await service.DeleteAsync(request);

        Assert.Equal(CustomLoopTraceDeletionStatus.CommittedWithAuditWarning, first.Status);
        Assert.Equal(CustomLoopTraceDeletionIntegrity.CommittedWithAuditWarning, first.Tombstone!.OutcomeIntegrity);
        Assert.Equal(CustomLoopTraceDeletionStatus.CommittedWithAuditWarning, replay.Status);
        Assert.Equal(attemptsAfterFirst, audit.Attempts);
        Assert.Equal(1, store.DeleteCalls);
        Assert.Equal(new[] { CustomLoopTraceDeletionIntegrity.OutcomeAuditStarted, CustomLoopTraceDeletionIntegrity.CommittedWithAuditWarning }, store.MarkedIntegrities);
    }

    [Fact]
    public async Task Interrupted_outcome_audit_is_failed_closed_without_duplicate_audit()
    {
        var request = Request();
        var tombstone = Tombstone(request, CustomLoopTraceDeletionIntegrity.OutcomeAuditStarted);
        var operation = Operation(request, tombstone, CustomLoopTraceDeletionIntegrity.OutcomeAuditStarted);
        var store = new RecordingStore(null, null) { Operation = operation };
        var audit = new RecordingAuditLog();

        var result = await new CustomLoopTraceRetentionService(store, audit).DeleteAsync(request);

        Assert.Equal(CustomLoopTraceDeletionStatus.CommittedWithAuditWarning, result.Status);
        Assert.Equal(CustomLoopTraceDeletionIntegrity.CommittedWithAuditWarning, result.Tombstone!.OutcomeIntegrity);
        Assert.Empty(audit.Events);
        Assert.Equal(0, audit.Attempts);
        Assert.Equal(new[] { CustomLoopTraceDeletionIntegrity.CommittedWithAuditWarning }, store.MarkedIntegrities);
    }

    [Fact]
    public async Task Active_outcome_audit_owner_is_not_overwritten_by_an_overlapping_replay()
    {
        var request = Request();
        var tombstone = Tombstone(request, CustomLoopTraceDeletionIntegrity.OutcomeAuditStarted);
        var operation = Operation(request, tombstone, CustomLoopTraceDeletionIntegrity.OutcomeAuditStarted);
        var store = new RecordingStore(null, null) { Operation = operation };
        var audit = new RecordingAuditLog();
        var now = operation.UpdatedAtUtc.AddSeconds(15);

        var result = await new CustomLoopTraceRetentionService(store, audit, new FixedTimeProvider(now)).DeleteAsync(request);

        Assert.Equal(CustomLoopTraceDeletionStatus.CommittedWithAuditWarning, result.Status);
        Assert.Equal(CustomLoopTraceDeletionIntegrity.OutcomeAuditStarted, result.Tombstone!.OutcomeIntegrity);
        Assert.Contains("still active", result.Detail, StringComparison.Ordinal);
        Assert.Empty(store.MarkedIntegrities);
        Assert.Equal(0, audit.Attempts);
    }

    [Fact]
    public async Task Future_outcome_audit_timestamp_is_not_treated_as_an_active_owner()
    {
        var request = Request();
        var tombstone = Tombstone(request, CustomLoopTraceDeletionIntegrity.OutcomeAuditStarted);
        var operation = Operation(request, tombstone, CustomLoopTraceDeletionIntegrity.OutcomeAuditStarted);
        var store = new RecordingStore(null, null) { Operation = operation };
        var now = operation.UpdatedAtUtc.AddSeconds(-1);

        var result = await new CustomLoopTraceRetentionService(store, new RecordingAuditLog(), new FixedTimeProvider(now)).DeleteAsync(request);

        Assert.Equal(CustomLoopTraceDeletionStatus.CommittedWithAuditWarning, result.Status);
        Assert.Equal(CustomLoopTraceDeletionIntegrity.CommittedWithAuditWarning, result.Tombstone!.OutcomeIntegrity);
        Assert.Equal(new[] { CustomLoopTraceDeletionIntegrity.CommittedWithAuditWarning }, store.MarkedIntegrities);
        Assert.DoesNotContain("still active", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stale_recovery_reports_a_concurrently_completed_outcome_owner()
    {
        var request = Request();
        var startedTombstone = Tombstone(request, CustomLoopTraceDeletionIntegrity.OutcomeAuditStarted);
        var started = Operation(request, startedTombstone, CustomLoopTraceDeletionIntegrity.OutcomeAuditStarted);
        var completedTombstone = Tombstone(request, CustomLoopTraceDeletionIntegrity.Complete);
        var store = new RecordingStore(null, null)
        {
            Operation = started,
            OperationWhenAlreadyMarked = Operation(request, completedTombstone, CustomLoopTraceDeletionIntegrity.Complete)
        };
        store.MarkStatuses.Enqueue(CustomLoopTraceDeletionAuditMarkStatus.AlreadyMarked);
        var now = started.UpdatedAtUtc.AddSeconds(31);

        var result = await new CustomLoopTraceRetentionService(store, new RecordingAuditLog(), new FixedTimeProvider(now)).DeleteAsync(request);

        Assert.Equal(CustomLoopTraceDeletionStatus.Replayed, result.Status);
        Assert.Equal(CustomLoopTraceDeletionIntegrity.Complete, result.Tombstone!.OutcomeIntegrity);
        Assert.Empty(store.MarkedIntegrities);
    }

    [Fact]
    public async Task Same_operation_changed_request_conflicts_without_audit_or_mutation()
    {
        var original = Request();
        var tombstone = Tombstone(original, CustomLoopTraceDeletionIntegrity.Complete);
        var store = new RecordingStore(null, null) { Operation = Operation(original, tombstone, CustomLoopTraceDeletionIntegrity.Complete) };
        var audit = new RecordingAuditLog();

        var result = await new CustomLoopTraceRetentionService(store, audit).DeleteAsync(original with { Actor = "actor-other" });

        Assert.Equal(CustomLoopTraceDeletionStatus.Conflict, result.Status);
        Assert.Equal(0, store.DeleteCalls);
        Assert.Equal(0, audit.Attempts);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("nonterminal")]
    [InlineData("hash-mismatch")]
    public async Task Safe_precondition_rejections_do_not_audit_an_intent_or_mutate(string scenario)
    {
        var inspection = scenario switch
        {
            "missing" => null,
            "nonterminal" => Inspection() with { TerminalStatus = CustomLoopRunStatus.Running, CompletedAtUtc = null },
            _ => Inspection() with { PersistedArtifactHash = new string('b', CustomLoopLimits.Sha256HexCharacters) }
        };
        var store = new RecordingStore(inspection, null);
        var audit = new RecordingAuditLog();

        var result = await new CustomLoopTraceRetentionService(store, audit).DeleteAsync(Request());

        Assert.Equal(scenario switch
        {
            "missing" => CustomLoopTraceDeletionStatus.NotFound,
            "nonterminal" => CustomLoopTraceDeletionStatus.Nonterminal,
            _ => CustomLoopTraceDeletionStatus.HashMismatch
        }, result.Status);
        Assert.Equal(0, store.DeleteCalls);
        Assert.Equal(0, audit.Attempts);
    }

    [Theory]
    [InlineData("../escape", TraceHash, "delete-trace", "actor-user", "web")]
    [InlineData("run-alpha", "BAD", "delete-trace", "actor-user", "web")]
    [InlineData("run-alpha", TraceHash, "UPPER", "actor-user", "web")]
    [InlineData("run-alpha", TraceHash, "delete-trace", "actor user", "web")]
    [InlineData("run-alpha", TraceHash, "delete-trace", "actor-user", "Web")]
    public async Task Invalid_authenticated_envelope_is_rejected_before_store_or_audit(string runId, string hash, string operationId, string actor, string surface)
    {
        var store = new RecordingStore(Inspection(), null);
        var audit = new RecordingAuditLog();

        var result = await new CustomLoopTraceRetentionService(store, audit).DeleteAsync(new CustomLoopTraceDeletionRequest(runId, hash, operationId, actor, surface));

        Assert.Equal(CustomLoopTraceDeletionStatus.Invalid, result.Status);
        Assert.Equal(0, store.LookupCalls);
        Assert.Equal(0, audit.Attempts);
    }

    [Fact]
    public async Task Inspection_and_quota_queries_delegate_to_the_store()
    {
        var inspection = Inspection();
        var quota = CustomLoopTraceQuota.Empty() with { RetainedTraceCount = 2, ActualTraceUtf8Bytes = 4096 };
        var store = new RecordingStore(inspection, null) { Quota = quota };
        var service = new CustomLoopTraceRetentionService(store, new RecordingAuditLog());

        var inspected = await service.InspectAsync(inspection.RunId);
        var observedQuota = await service.GetQuotaAsync();

        Assert.Same(inspection, inspected);
        Assert.Same(quota, observedQuota);
        Assert.Equal(1, store.InspectCalls);
        Assert.Equal(1, store.QuotaCalls);
    }

    [Theory]
    [InlineData("ledger")]
    [InlineData("inspection")]
    public async Task Store_read_failures_are_reported_without_audit_or_mutation(string failureStage)
    {
        var store = new RecordingStore(Inspection(), null);
        if (failureStage == "ledger")
        {
            store.LookupException = new IOException("ledger unavailable");
        }
        else
        {
            store.InspectException = new IOException("trace unavailable");
        }

        var audit = new RecordingAuditLog();

        var result = await new CustomLoopTraceRetentionService(store, audit).DeleteAsync(Request());

        Assert.Equal(CustomLoopTraceDeletionStatus.Invalid, result.Status);
        Assert.Contains(nameof(IOException), result.Detail, StringComparison.Ordinal);
        Assert.Equal(0, store.DeleteCalls);
        Assert.Equal(0, audit.Attempts);
    }

    [Fact]
    public async Task Unsupported_operation_ledger_state_is_rejected_without_mutation()
    {
        var request = Request();
        var operation = Operation(request, Tombstone(request, CustomLoopTraceDeletionIntegrity.Unknown), CustomLoopTraceDeletionIntegrity.Unknown);
        var store = new RecordingStore(null, null)
        {
            LookupOverride = new CustomLoopTraceDeletionLookupResult((CustomLoopTraceDeletionLookupStatus)0, operation)
        };

        var result = await new CustomLoopTraceRetentionService(store, new RecordingAuditLog()).DeleteAsync(request);

        Assert.Equal(CustomLoopTraceDeletionStatus.Invalid, result.Status);
        Assert.Equal(0, store.DeleteCalls);
    }

    [Fact]
    public async Task Previously_deleted_trace_conflicts_without_repeating_audit_or_mutation()
    {
        var request = Request();
        var tombstone = Tombstone(request, CustomLoopTraceDeletionIntegrity.Complete);
        var deleted = Inspection() with { Kind = CustomLoopTraceArtifactKind.Tombstone, Tombstone = tombstone };
        var store = new RecordingStore(deleted, null);
        var audit = new RecordingAuditLog();

        var result = await new CustomLoopTraceRetentionService(store, audit).DeleteAsync(request);

        Assert.Equal(CustomLoopTraceDeletionStatus.Conflict, result.Status);
        Assert.Same(tombstone, result.Tombstone);
        Assert.Equal(0, store.DeleteCalls);
        Assert.Equal(0, audit.Attempts);
    }

    [Theory]
    [InlineData(CustomLoopTraceDeletionStoreStatus.NotFound, CustomLoopTraceDeletionStatus.NotFound)]
    [InlineData(CustomLoopTraceDeletionStoreStatus.Nonterminal, CustomLoopTraceDeletionStatus.Nonterminal)]
    [InlineData(CustomLoopTraceDeletionStoreStatus.HashMismatch, CustomLoopTraceDeletionStatus.HashMismatch)]
    [InlineData(CustomLoopTraceDeletionStoreStatus.OperationConflict, CustomLoopTraceDeletionStatus.Conflict)]
    [InlineData(CustomLoopTraceDeletionStoreStatus.TombstoneLimitExceeded, CustomLoopTraceDeletionStatus.LimitExceeded)]
    [InlineData(CustomLoopTraceDeletionStoreStatus.DeletionOperationLimitExceeded, CustomLoopTraceDeletionStatus.OperationLimitExceeded)]
    [InlineData(CustomLoopTraceDeletionStoreStatus.Unknown, CustomLoopTraceDeletionStatus.Invalid)]
    public async Task Post_intent_store_rejections_are_mapped_with_a_durable_terminal_outcome(CustomLoopTraceDeletionStoreStatus storeStatus, CustomLoopTraceDeletionStatus expectedStatus)
    {
        var request = Request();
        var tombstone = storeStatus == CustomLoopTraceDeletionStoreStatus.OperationConflict ? Tombstone(request, CustomLoopTraceDeletionIntegrity.Unknown) : null;
        var hasCommittedOutcome = storeStatus is not CustomLoopTraceDeletionStoreStatus.Unknown and not CustomLoopTraceDeletionStoreStatus.DeletionOperationLimitExceeded;
        var integrity = hasCommittedOutcome ? CustomLoopTraceDeletionIntegrity.PendingOutcomeAudit : CustomLoopTraceDeletionIntegrity.Unknown;
        var store = new RecordingStore(Inspection(), new CustomLoopTraceDeletionStoreResult(storeStatus, tombstone, integrity));
        var audit = new RecordingAuditLog();

        var result = await new CustomLoopTraceRetentionService(store, audit).DeleteAsync(request);

        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(1, store.DeleteCalls);
        Assert.Equal(AuditSchema.Actions.LoopTraceDeletionIntent, audit.Events[0].Action);
        if (hasCommittedOutcome)
        {
            Assert.Equal(new[] { AuditSchema.Actions.LoopTraceDeletionIntent, AuditSchema.Actions.LoopTraceDeletionOutcome }, audit.Events.Select(item => item.Action));
            Assert.Equal(new[] { CustomLoopTraceDeletionIntegrity.OutcomeAuditStarted, CustomLoopTraceDeletionIntegrity.Complete }, store.MarkedIntegrities);
        }
        else
        {
            Assert.Single(audit.Events);
            Assert.Empty(store.MarkedIntegrities);
        }
    }

    [Fact]
    public async Task Mutation_failure_reports_committed_when_the_matching_tombstone_is_observable()
    {
        var request = Request();
        var tombstone = Tombstone(request, CustomLoopTraceDeletionIntegrity.PendingOutcomeAudit);
        var store = new RecordingStore(Inspection(), null)
        {
            DeleteException = new IOException("write response lost"),
            InspectionAfterDeleteFailure = Inspection() with { Kind = CustomLoopTraceArtifactKind.Tombstone, Tombstone = tombstone }
        };

        var result = await new CustomLoopTraceRetentionService(store, new RecordingAuditLog()).DeleteAsync(request);

        Assert.Equal(CustomLoopTraceDeletionStatus.CommittedWithAuditWarning, result.Status);
        Assert.Same(tombstone, result.Tombstone);
        Assert.Equal(2, store.DeleteCalls);
        Assert.Equal(2, store.InspectCalls);
        Assert.False(store.RecoveryReusedMutationToken);
    }

    [Fact]
    public async Task Tombstone_first_response_loss_reconciles_the_operation_and_completes_outcome_audit()
    {
        var request = Request();
        var tombstone = Tombstone(request, CustomLoopTraceDeletionIntegrity.PendingOutcomeAudit);
        var stored = new CustomLoopTraceDeletionStoreResult(CustomLoopTraceDeletionStoreStatus.Deleted, tombstone, CustomLoopTraceDeletionIntegrity.PendingOutcomeAudit);
        var store = new RecordingStore(Inspection(), stored)
        {
            DeleteException = new IOException("tombstone write response lost"),
            DeleteExceptionOnlyOnFirstAttempt = true,
            InspectionAfterDeleteFailure = Inspection() with { Kind = CustomLoopTraceArtifactKind.Tombstone, Tombstone = tombstone }
        };
        var audit = new RecordingAuditLog();

        var result = await new CustomLoopTraceRetentionService(store, audit).DeleteAsync(request);

        Assert.Equal(CustomLoopTraceDeletionStatus.Replayed, result.Status);
        Assert.Equal(CustomLoopTraceDeletionIntegrity.Complete, result.Tombstone!.OutcomeIntegrity);
        Assert.Equal(2, store.DeleteCalls);
        Assert.Equal(CustomLoopTraceDeletionOperationState.OutcomeCommitted, store.Operation!.State);
        Assert.Equal(new[] { AuditSchema.Actions.LoopTraceDeletionIntent, AuditSchema.Actions.LoopTraceDeletionOutcome }, audit.Events.Select(item => item.Action));
    }

    [Fact]
    public async Task Mutation_and_recovery_read_failure_reports_invalid_without_claiming_commit()
    {
        var store = new RecordingStore(Inspection(), null)
        {
            DeleteException = new IOException("write failed"),
            InspectExceptionAfterDeleteFailure = new IOException("recovery read failed")
        };

        var result = await new CustomLoopTraceRetentionService(store, new RecordingAuditLog()).DeleteAsync(Request());

        Assert.Equal(CustomLoopTraceDeletionStatus.Invalid, result.Status);
        Assert.Null(result.Tombstone);
        Assert.Contains(nameof(IOException), result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Malformed_committed_store_result_without_tombstone_fails_closed()
    {
        var malformed = new CustomLoopTraceDeletionStoreResult(CustomLoopTraceDeletionStoreStatus.Deleted, null, CustomLoopTraceDeletionIntegrity.PendingOutcomeAudit);
        var store = new RecordingStore(Inspection(), malformed);

        var result = await new CustomLoopTraceRetentionService(store, new RecordingAuditLog()).DeleteAsync(Request());

        Assert.Equal(CustomLoopTraceDeletionStatus.Invalid, result.Status);
        Assert.Null(result.Tombstone);
    }

    [Fact]
    public async Task Unknown_committed_integrity_remains_visible_without_repeating_audit_or_mutation()
    {
        var request = Request();
        var tombstone = Tombstone(request, CustomLoopTraceDeletionIntegrity.Unknown);
        var store = new RecordingStore(null, null) { Operation = Operation(request, tombstone, CustomLoopTraceDeletionIntegrity.Unknown) };
        var audit = new RecordingAuditLog();

        var result = await new CustomLoopTraceRetentionService(store, audit).DeleteAsync(request);

        Assert.Equal(CustomLoopTraceDeletionStatus.CommittedWithAuditWarning, result.Status);
        Assert.Same(tombstone, result.Tombstone);
        Assert.Equal(0, store.DeleteCalls);
        Assert.Equal(0, audit.Attempts);
    }

    [Theory]
    [InlineData(CustomLoopTraceDeletionIntegrity.Complete, CustomLoopTraceDeletionStatus.Replayed)]
    [InlineData(CustomLoopTraceDeletionIntegrity.CommittedWithAuditWarning, CustomLoopTraceDeletionStatus.CommittedWithAuditWarning)]
    [InlineData(CustomLoopTraceDeletionIntegrity.OutcomeAuditStarted, CustomLoopTraceDeletionStatus.CommittedWithAuditWarning)]
    public async Task Existing_outcome_owner_is_observed_without_duplicate_outcome_audit(CustomLoopTraceDeletionIntegrity ownerIntegrity, CustomLoopTraceDeletionStatus expectedStatus)
    {
        var request = Request();
        var pendingTombstone = Tombstone(request, CustomLoopTraceDeletionIntegrity.PendingOutcomeAudit);
        var ownerTombstone = Tombstone(request, ownerIntegrity);
        var ownerOperation = Operation(request, ownerTombstone, ownerIntegrity);
        var store = new RecordingStore(Inspection(), new CustomLoopTraceDeletionStoreResult(CustomLoopTraceDeletionStoreStatus.Deleted, pendingTombstone, CustomLoopTraceDeletionIntegrity.PendingOutcomeAudit))
        {
            OperationWhenAlreadyMarked = ownerOperation
        };
        store.MarkStatuses.Enqueue(CustomLoopTraceDeletionAuditMarkStatus.AlreadyMarked);
        var audit = new RecordingAuditLog();

        var result = await new CustomLoopTraceRetentionService(store, audit).DeleteAsync(request);

        Assert.Equal(expectedStatus, result.Status);
        var expectedTombstone = ownerIntegrity == CustomLoopTraceDeletionIntegrity.OutcomeAuditStarted
            ? ownerTombstone with { OutcomeIntegrity = CustomLoopTraceDeletionIntegrity.CommittedWithAuditWarning }
            : ownerTombstone;
        Assert.Equal(expectedTombstone, result.Tombstone);
        Assert.Single(audit.Events);
        Assert.Equal(AuditSchema.Actions.LoopTraceDeletionIntent, audit.Events[0].Action);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Outcome_audit_start_failure_preserves_committed_warning(bool throws)
    {
        var request = Request();
        var tombstone = Tombstone(request, CustomLoopTraceDeletionIntegrity.PendingOutcomeAudit);
        var store = new RecordingStore(Inspection(), new CustomLoopTraceDeletionStoreResult(CustomLoopTraceDeletionStoreStatus.Deleted, tombstone, CustomLoopTraceDeletionIntegrity.PendingOutcomeAudit));
        if (throws)
        {
            store.ThrowOnMarkAttempt = 1;
        }
        else
        {
            store.MarkStatuses.Enqueue(CustomLoopTraceDeletionAuditMarkStatus.NotFound);
        }

        var result = await new CustomLoopTraceRetentionService(store, new RecordingAuditLog()).DeleteAsync(request);

        Assert.Equal(CustomLoopTraceDeletionStatus.CommittedWithAuditWarning, result.Status);
        Assert.Same(tombstone, result.Tombstone);
        Assert.Equal(1, store.MarkAttempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Outcome_integrity_completion_failure_preserves_committed_warning(bool throws)
    {
        var request = Request();
        var tombstone = Tombstone(request, CustomLoopTraceDeletionIntegrity.PendingOutcomeAudit);
        var store = new RecordingStore(Inspection(), new CustomLoopTraceDeletionStoreResult(CustomLoopTraceDeletionStoreStatus.Deleted, tombstone, CustomLoopTraceDeletionIntegrity.PendingOutcomeAudit));
        store.MarkStatuses.Enqueue(CustomLoopTraceDeletionAuditMarkStatus.Marked);
        if (throws)
        {
            store.ThrowOnMarkAttempt = 2;
        }
        else
        {
            store.MarkStatuses.Enqueue(CustomLoopTraceDeletionAuditMarkStatus.NotFound);
        }

        var result = await new CustomLoopTraceRetentionService(store, new RecordingAuditLog()).DeleteAsync(request);

        Assert.Equal(CustomLoopTraceDeletionStatus.CommittedWithAuditWarning, result.Status);
        Assert.Same(tombstone, result.Tombstone);
        Assert.Equal(2, store.MarkAttempts);
    }

    [Fact]
    public async Task Interrupted_outcome_warning_marker_failure_does_not_duplicate_audit()
    {
        var request = Request();
        var tombstone = Tombstone(request, CustomLoopTraceDeletionIntegrity.OutcomeAuditStarted);
        var store = new RecordingStore(null, null)
        {
            Operation = Operation(request, tombstone, CustomLoopTraceDeletionIntegrity.OutcomeAuditStarted),
            ThrowOnMarkAttempt = 1
        };
        var audit = new RecordingAuditLog();

        var result = await new CustomLoopTraceRetentionService(store, audit).DeleteAsync(request);

        Assert.Equal(CustomLoopTraceDeletionStatus.CommittedWithAuditWarning, result.Status);
        Assert.Same(tombstone, result.Tombstone);
        Assert.Equal(0, audit.Attempts);
        Assert.Equal(1, store.MarkAttempts);
    }

    private static CustomLoopTraceDeletionRequest Request() => new("run-alpha", TraceHash, "delete-trace", "actor-user", "web");

    private static CustomLoopTraceInspection Inspection() => new(CustomLoopTraceArtifactKind.LiveTrace, "run-alpha", "loop-alpha", CustomLoopRunStatus.Completed, 2, new string('b', 64), TraceHash, 4096, TraceHash, 4096, _timestamp, _timestamp.AddMinutes(2), null);

    private static CustomLoopTraceTombstone Tombstone(CustomLoopTraceDeletionRequest request, CustomLoopTraceDeletionIntegrity integrity)
    {
        return new CustomLoopTraceTombstone(CustomLoopTraceTombstone.CurrentSchemaVersion, CustomLoopTraceTombstone.CurrentArtifactKind, request.RunId, "loop-alpha", "invoke-alpha", new string('c', 64), CustomLoopRunStatus.Completed, 2, new string('b', 64), request.ExpectedTraceHash, 4096, _timestamp, _timestamp.AddMinutes(2), _timestamp.AddMinutes(3), request.Actor, request.Surface, request.OperationId, CustomLoopTraceDeletionRequestHash.Compute(request), request.OperationId, request.OperationId, integrity);
    }

    private static CustomLoopTraceDeletionOperation Operation(CustomLoopTraceDeletionRequest request, CustomLoopTraceTombstone tombstone, CustomLoopTraceDeletionIntegrity integrity)
    {
        return new CustomLoopTraceDeletionOperation(CustomLoopTraceDeletionOperation.CurrentSchemaVersion, request.OperationId, CustomLoopTraceDeletionRequestHash.Compute(request), request, _timestamp.AddMinutes(3), _timestamp.AddMinutes(3), CustomLoopTraceDeletionOperationState.OutcomeCommitted, CustomLoopTraceDeletionStoreStatus.Deleted, tombstone, integrity);
    }

    private static CustomLoopTraceDeletionOperation PendingOperation(CustomLoopTraceDeletionRequest request, DateTimeOffset timestamp)
    {
        return new CustomLoopTraceDeletionOperation(CustomLoopTraceDeletionOperation.CurrentSchemaVersion, request.OperationId, CustomLoopTraceDeletionRequestHash.Compute(request), request, timestamp, timestamp, CustomLoopTraceDeletionOperationState.PendingMutation, CustomLoopTraceDeletionStoreStatus.Unknown, null, CustomLoopTraceDeletionIntegrity.Unknown);
    }

    private sealed class BlockingIntentAuditLog : IAuditLog
    {
        private readonly TaskCompletionSource _intentStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseIntent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task IntentStarted => _intentStarted.Task;
        public List<AuditEvent> Events { get; } = [];

        public CancellationToken IntentCancellationToken { get; private set; }

        public async Task AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            if (auditEvent.Action == AuditSchema.Actions.LoopTraceDeletionIntent)
            {
                IntentCancellationToken = cancellationToken;
                _intentStarted.TrySetResult();
                await _releaseIntent.Task.WaitAsync(cancellationToken);
            }

            Events.Add(auditEvent);
        }

        public void ReleaseIntent() => _releaseIntent.TrySetResult();

        public Task<IReadOnlyList<AuditEvent>> ReadTailAsync(int limit, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AuditEvent>>(Events.TakeLast(limit).ToArray());
    }

    private sealed class RecordingAuditLog : IAuditLog
    {
        private readonly int? _failOnAttempt;
        public RecordingAuditLog(int? failOnAttempt = null) => _failOnAttempt = failOnAttempt;
        public List<AuditEvent> Events { get; } = [];
        public int Attempts { get; private set; }

        public Task AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Attempts++;
            if (_failOnAttempt == Attempts)
            {
                throw new IOException("audit unavailable");
            }

            Events.Add(auditEvent);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AuditEvent>> ReadTailAsync(int limit, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AuditEvent>>(Events.TakeLast(limit).ToArray());
    }

    private sealed class RecordingStore : ICustomLoopRunStore
    {
        private readonly CustomLoopTraceInspection? _inspection;
        private readonly CustomLoopTraceDeletionStoreResult? _deleteResult;
        public RecordingStore(CustomLoopTraceInspection? inspection, CustomLoopTraceDeletionStoreResult? deleteResult)
        {
            _inspection = inspection;
            _deleteResult = deleteResult;
        }

        public CustomLoopTraceDeletionOperation? Operation { get; set; }
        public CustomLoopTraceDeletionLookupResult? LookupOverride { get; set; }
        public CustomLoopTraceDeletionOperation? OperationWhenAlreadyMarked { get; set; }
        public CustomLoopTraceInspection? InspectionAfterDeleteFailure { get; set; }
        public CustomLoopTraceQuota Quota { get; set; } = CustomLoopTraceQuota.Empty();
        public Exception? LookupException { get; set; }
        public Exception? InspectException { get; set; }
        public Exception? InspectExceptionAfterDeleteFailure { get; set; }
        public Exception? ReserveException { get; set; }
        public bool ReserveBeforeThrow { get; set; }
        public Exception? DeleteException { get; set; }
        public Exception? CommitAuditFailureException { get; set; }
        public bool DeleteExceptionOnlyOnFirstAttempt { get; set; }
        public int? ThrowOnMarkAttempt { get; set; }
        public Queue<CustomLoopTraceDeletionAuditMarkStatus> MarkStatuses { get; } = [];
        public List<CustomLoopTraceDeletionIntegrity> MarkedIntegrities { get; } = [];
        public int DeleteCalls { get; private set; }
        public int LookupCalls { get; private set; }
        public int InspectCalls { get; private set; }
        public int ReserveCalls { get; private set; }
        public int QuotaCalls { get; private set; }
        public int MarkAttempts { get; private set; }
        public bool RecoveryReusedMutationToken { get; private set; }
        public CancellationToken DeletionCancellationToken => _mutationToken;
        private CancellationToken _mutationToken;

        public Task<CustomLoopTraceQuota> GetTraceQuotaAsync(CancellationToken cancellationToken = default)
        {
            QuotaCalls++;
            return Task.FromResult(Quota);
        }

        public Task<CustomLoopTraceInspection?> InspectTraceAsync(string runId, CancellationToken cancellationToken = default)
        {
            InspectCalls++;
            if (DeleteCalls > 0)
            {
                RecoveryReusedMutationToken = cancellationToken == _mutationToken;
            }

            var exception = DeleteCalls > 0 ? InspectExceptionAfterDeleteFailure ?? InspectException : InspectException;
            if (exception is not null)
            {
                throw exception;
            }

            var inspection = DeleteCalls > 0 && InspectionAfterDeleteFailure is not null ? InspectionAfterDeleteFailure : _inspection;
            return Task.FromResult(inspection);
        }

        public Task<CustomLoopTraceDeletionLookupResult> GetTraceDeletionOperationAsync(string operationId, CancellationToken cancellationToken = default)
        {
            LookupCalls++;
            if (LookupException is not null)
            {
                throw LookupException;
            }

            return Task.FromResult(LookupOverride ?? (Operation is null ? CustomLoopTraceDeletionLookupResult.NotFound() : CustomLoopTraceDeletionLookupResult.Found(Operation)));
        }

        public Task<CustomLoopTraceDeletionReservationResult> ReserveTraceDeletionOperationAsync(CustomLoopTraceDeletionMutation mutation, CancellationToken cancellationToken = default)
        {
            ReserveCalls++;
            if (ReserveException is not null && !ReserveBeforeThrow)
            {
                throw ReserveException;
            }

            if (Operation is not null)
            {
                var status = Operation.Request == mutation.Request && string.Equals(Operation.RequestHash, mutation.RequestHash, StringComparison.Ordinal)
                    ? Operation.State == CustomLoopTraceDeletionOperationState.PendingMutation
                        ? CustomLoopTraceDeletionReservationStatus.Pending
                        : CustomLoopTraceDeletionReservationStatus.OutcomeCommitted
                    : CustomLoopTraceDeletionReservationStatus.OperationConflict;
                return Task.FromResult(new CustomLoopTraceDeletionReservationResult(status, Operation));
            }

            Operation = new CustomLoopTraceDeletionOperation(CustomLoopTraceDeletionOperation.CurrentSchemaVersion, mutation.Request.OperationId, mutation.RequestHash, mutation.Request, mutation.RequestedAtUtc, mutation.RequestedAtUtc, CustomLoopTraceDeletionOperationState.PendingMutation, CustomLoopTraceDeletionStoreStatus.Unknown, null, CustomLoopTraceDeletionIntegrity.Unknown);
            if (ReserveException is not null)
            {
                throw ReserveException;
            }

            return Task.FromResult(new CustomLoopTraceDeletionReservationResult(CustomLoopTraceDeletionReservationStatus.Reserved, Operation));
        }

        public Task<CustomLoopTraceDeletionStoreResult> CommitTraceDeletionAuditFailureAsync(CustomLoopTraceDeletionMutation mutation, CancellationToken cancellationToken = default)
        {
            if (CommitAuditFailureException is not null)
            {
                throw CommitAuditFailureException;
            }

            if (Operation is null)
            {
                return Task.FromResult(new CustomLoopTraceDeletionStoreResult(CustomLoopTraceDeletionStoreStatus.Unknown, null, CustomLoopTraceDeletionIntegrity.Unknown));
            }

            if (Operation.State == CustomLoopTraceDeletionOperationState.OutcomeCommitted)
            {
                return Task.FromResult(Operation.ToStoreResult());
            }

            var result = new CustomLoopTraceDeletionStoreResult(CustomLoopTraceDeletionStoreStatus.AuditUnavailable, null, CustomLoopTraceDeletionIntegrity.PendingOutcomeAudit);
            Operation = Operation with { State = CustomLoopTraceDeletionOperationState.OutcomeCommitted, Outcome = result.Status, Integrity = result.Integrity };
            return Task.FromResult(result);
        }

        public Task<CustomLoopTraceDeletionStoreResult> DeleteTerminalTraceAsync(CustomLoopTraceDeletionMutation mutation, CancellationToken cancellationToken = default)
        {
            DeleteCalls++;
            _mutationToken = cancellationToken;
            if (DeleteException is not null && (!DeleteExceptionOnlyOnFirstAttempt || DeleteCalls == 1))
            {
                throw DeleteException;
            }

            var result = _deleteResult ?? throw new InvalidOperationException("Delete was not expected.");
            Operation = new CustomLoopTraceDeletionOperation(CustomLoopTraceDeletionOperation.CurrentSchemaVersion, mutation.Request.OperationId, mutation.RequestHash, mutation.Request, mutation.RequestedAtUtc, mutation.RequestedAtUtc, CustomLoopTraceDeletionOperationState.OutcomeCommitted, result.Status, result.Tombstone, result.Integrity);
            return Task.FromResult(result);
        }

        public Task<CustomLoopTraceDeletionAuditMarkStatus> MarkTraceDeletionOutcomeAsync(string operationId, CustomLoopTraceDeletionIntegrity integrity, CancellationToken cancellationToken = default)
        {
            MarkAttempts++;
            if (ThrowOnMarkAttempt == MarkAttempts)
            {
                throw new IOException("integrity marker unavailable");
            }

            if (Operation is null)
            {
                return Task.FromResult(CustomLoopTraceDeletionAuditMarkStatus.NotFound);
            }

            var status = MarkStatuses.Count == 0 ? CustomLoopTraceDeletionAuditMarkStatus.Marked : MarkStatuses.Dequeue();
            if (status == CustomLoopTraceDeletionAuditMarkStatus.AlreadyMarked && OperationWhenAlreadyMarked is not null)
            {
                Operation = OperationWhenAlreadyMarked;
            }

            if (status != CustomLoopTraceDeletionAuditMarkStatus.Marked)
            {
                return Task.FromResult(status);
            }

            MarkedIntegrities.Add(integrity);
            var tombstone = Operation.Tombstone is null ? null : Operation.Tombstone with { OutcomeIntegrity = integrity };
            Operation = Operation with { Tombstone = tombstone, Integrity = integrity };
            return Task.FromResult(CustomLoopTraceDeletionAuditMarkStatus.Marked);
        }

        public Task<CustomLoopRunStoreResult> CreateAsync(CustomLoopRunRecord run, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CustomLoopRunRecord?> GetAsync(string runId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CustomLoopRunRecord?> GetByAdmissionOperationAsync(string admissionOperationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CustomLoopRunRecord?> GetNonterminalByLoopAsync(string loopId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CustomLoopRunSummary>> ListRecentAsync(int maximumCount, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CustomLoopRunRecord>> ListNonterminalAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CustomLoopRunStoreResult> UpdateAsync(CustomLoopRunRecord run, int expectedLifecycleVersion, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class SequencedTimeProvider(params DateTimeOffset[] values) : TimeProvider
    {
        private int _index;

        public override DateTimeOffset GetUtcNow()
        {
            var index = Math.Min(Interlocked.Increment(ref _index) - 1, values.Length - 1);
            return values[index];
        }
    }
}
