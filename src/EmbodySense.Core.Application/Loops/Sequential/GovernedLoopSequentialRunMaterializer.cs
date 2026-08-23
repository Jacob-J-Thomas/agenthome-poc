using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.Loops.Admission;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Sequential;
using EmbodySense.Core.Common.Triggers;
using EmbodySense.Core.Common.Triggers.Models;

namespace EmbodySense.Core.Application.Loops.Sequential;

/// <summary>Materializes one committed canonical admission into the existing durable ordered-run store without dispatching execution.</summary>
public sealed class GovernedLoopSequentialRunMaterializer : IGovernedLoopSequentialRunMaterializer
{
    private static readonly TimeSpan _integrityWriteTimeout = TimeSpan.FromSeconds(30);
    private const string AdmissionDetail = "The exact canonical graph, invocation snapshot, admission receipt, and completed Trigger outcome were materialized before provider dispatch.";
    private const string AuditMarkerDetail = "The matching canonical admission outcome audit is durable; provider dispatch may now be considered.";
    private readonly ICustomLoopRunStore _runStore;
    private readonly IGovernedLoopSequentialAuditRecorder _auditRecorder;
    private readonly IGovernedLoopSequentialEventIdentityGenerator _eventIdentityGenerator;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates an admission materializer over caller-owned durable ports.</summary>
    public GovernedLoopSequentialRunMaterializer(
        ICustomLoopRunStore runStore,
        IGovernedLoopSequentialAuditRecorder auditRecorder,
        IGovernedLoopSequentialEventIdentityGenerator eventIdentityGenerator,
        TimeProvider? timeProvider = null)
    {
        _runStore = runStore ?? throw new ArgumentNullException(nameof(runStore));
        _auditRecorder = auditRecorder ?? throw new ArgumentNullException(nameof(auditRecorder));
        _eventIdentityGenerator = eventIdentityGenerator ?? throw new ArgumentNullException(nameof(eventIdentityGenerator));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Creates or reconciles one exact run and its crash-safe admission-audit completion boundary.</summary>
    public async Task<GovernedLoopSequentialMaterializationResult> MaterializeAsync(
        GovernedLoopSequentialMaterializationRequest? request,
        CancellationToken cancellationToken = default)
    {
        if (request is null || request.SchemaVersion != GovernedLoopSequentialMaterializationRequest.CurrentSchemaVersion)
        {
            return Result(GovernedLoopSequentialMaterializationStatus.Invalid, null, null, "The sequential materialization request is missing or has an unsupported schema.");
        }

        var anchorResult = GovernedLoopSequentialRunAnchorGuard.Create(
            request.AdapterBinding,
            request.AdmissionRequest,
            request.AdmissionReceipt,
            request.InvocationSnapshot,
            request.Artifact);
        if (anchorResult.Status != GovernedLoopSequentialRunAnchorStatus.Ready || anchorResult.Anchor is null)
        {
            return Result(GovernedLoopSequentialMaterializationStatus.Invalid, null, null, $"The exact canonical admission hand-off was rejected with `{anchorResult.Status}`.");
        }

        var projection = GovernedLoopSequentialLegacyDefinitionProjector.Project(
            request.AdapterBinding,
            request.InvocationSnapshot,
            request.Plan,
            request.Artifact);
        if (projection.Status != GovernedLoopSequentialLegacyDefinitionProjectionStatus.Ready || projection.Definition is null)
        {
            return Result(GovernedLoopSequentialMaterializationStatus.Invalid, null, anchorResult.Anchor, $"The ordered-runtime compatibility projection was rejected with `{projection.Status}`.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var existingRead = await ReadExistingAsync(request, cancellationToken).ConfigureAwait(false);
        if (existingRead.Status is not null)
        {
            return Result(existingRead.Status.Value, existingRead.Run, anchorResult.Anchor, existingRead.Detail!);
        }

        if (existingRead.Run is not null)
        {
            return await ReconcileExistingAsync(request, projection.Definition, anchorResult.Anchor, existingRead.Run, cancellationToken).ConfigureAwait(false);
        }

        var candidate = CreateRun(request, projection.Definition);
        var validation = CustomLoopRunValidator.Validate(candidate);
        if (!validation.IsValid)
        {
            var first = validation.Errors.FirstOrDefault();
            var evidence = first is null ? string.Empty : $" First failure: `{first.Code}` at `{first.Field}`.";
            return Result(GovernedLoopSequentialMaterializationStatus.Invalid, null, anchorResult.Anchor, "The projected canonical admission could not form a valid ordered-run record." + evidence);
        }

        CustomLoopRunStoreResult created;
        try
        {
            if (request.InvocationSnapshot.TriggerOrigin is not null)
            {
                var scheduled = await CreateScheduledAsync(request, candidate, cancellationToken).ConfigureAwait(false);
                var scheduledResult = await ResolveScheduledResultAsync(request, projection.Definition, anchorResult.Anchor, scheduled).ConfigureAwait(false);
                if (scheduledResult is not null)
                {
                    return scheduledResult;
                }

                created = CustomLoopRunStoreResult.Created(scheduled.Run!);
            }
            else
            {
                created = await _runStore.CreateAsync(candidate, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            if (request.InvocationSnapshot.TriggerOrigin is not null)
            {
                try
                {
                    var scheduled = await CreateScheduledAsync(request, candidate, CancellationToken.None).ConfigureAwait(false);
                    var scheduledResult = await ResolveScheduledResultAsync(request, projection.Definition, anchorResult.Anchor, scheduled).ConfigureAwait(false);
                    if (scheduledResult is not null)
                    {
                        return scheduledResult;
                    }

                    created = CustomLoopRunStoreResult.Created(scheduled.Run!);
                }
                catch
                {
                    return await ReconcileAfterPossibleCreateAsync(request, projection.Definition, anchorResult.Anchor, exception).ConfigureAwait(false);
                }
            }
            else
            {
                return await ReconcileAfterPossibleCreateAsync(request, projection.Definition, anchorResult.Anchor, exception).ConfigureAwait(false);
            }
        }

        var durable = created.Run;
        switch (created.Status)
        {
            case CustomLoopRunStoreStatus.Created when durable is not null:
                break;
            case CustomLoopRunStoreStatus.AlreadyCreated when durable is not null:
                return await ReconcileExistingAsync(request, projection.Definition, anchorResult.Anchor, durable, CancellationToken.None).ConfigureAwait(false);
            case CustomLoopRunStoreStatus.OperationConflict:
                return Result(GovernedLoopSequentialMaterializationStatus.Conflict, durable, anchorResult.Anchor, "The admission operation is already bound to different durable run evidence.");
            case CustomLoopRunStoreStatus.NonterminalRunExists:
                return Result(GovernedLoopSequentialMaterializationStatus.NonterminalRunExists, durable, anchorResult.Anchor, "A different nonterminal run already owns the canonical loop identity.");
            case CustomLoopRunStoreStatus.LimitExceeded:
                return Result(GovernedLoopSequentialMaterializationStatus.LimitExceeded, null, anchorResult.Anchor, "The bounded ordered-run trace limit rejected materialization.");
            default:
                return Result(GovernedLoopSequentialMaterializationStatus.Unavailable, durable, anchorResult.Anchor, $"The ordered-run store rejected materialization with `{created.Status}`.");
        }

        if (!MatchesExpectedAdmission(durable, request, projection.Definition))
        {
            return Result(GovernedLoopSequentialMaterializationStatus.Conflict, durable, anchorResult.Anchor, "The created ordered run did not retain the exact committed canonical admission coordinates.");
        }

        return await CompleteAuditBoundaryAsync(request, projection.Definition, anchorResult.Anchor, durable, replay: false).ConfigureAwait(false);
    }

    private async Task<ScheduleRunAdmissionStoreResult> CreateScheduledAsync(
        GovernedLoopSequentialMaterializationRequest request,
        CustomLoopRunRecord candidate,
        CancellationToken cancellationToken)
    {
        var canonical = request.InvocationSnapshot.TriggerOrigin!.CanonicalEnvelope;
        if (!TriggerDeliveryJson.TryDeserialize(canonical, out var envelope, out _) || envelope is null)
        {
            return new ScheduleRunAdmissionStoreResult(ScheduleRunAdmissionStoreStatus.Conflict, null, null);
        }

        return await _runStore.CreateScheduledAsync(candidate, envelope, cancellationToken).ConfigureAwait(false);
    }

    private async Task<GovernedLoopSequentialMaterializationResult?> ResolveScheduledResultAsync(
        GovernedLoopSequentialMaterializationRequest request,
        CustomLoopDefinition definition,
        GovernedLoopSequentialRunAnchor anchor,
        ScheduleRunAdmissionStoreResult result)
    {
        switch (result.Status)
        {
            case ScheduleRunAdmissionStoreStatus.Created when result.Run is not null:
                return null;
            case ScheduleRunAdmissionStoreStatus.Replayed when result.Run is not null:
                return await ReconcileMaterializedRunAsync(request, definition, anchor, result.Run, CancellationToken.None).ConfigureAwait(false);
            case ScheduleRunAdmissionStoreStatus.OverlapSkipped:
                return Result(GovernedLoopSequentialMaterializationStatus.OverlapSkipped, result.Run, anchor, "Atomic run admission durably skipped the exact occurrence behind the current nonterminal run.");
            case ScheduleRunAdmissionStoreStatus.OverlapDeferred:
                return Result(GovernedLoopSequentialMaterializationStatus.OverlapDeferred, result.Run, anchor, "Atomic run admission retained the exact DeferOne occurrence for bounded reselection.");
            case ScheduleRunAdmissionStoreStatus.OverlapSerialized:
                return Result(GovernedLoopSequentialMaterializationStatus.OverlapSerialized, result.Run, anchor, "Atomic run admission retained the exact Allow occurrence for serialized reselection.");
            case ScheduleRunAdmissionStoreStatus.DeferredOneSuppressed:
                return Result(GovernedLoopSequentialMaterializationStatus.DeferredOneSuppressed, result.Run, anchor, "Atomic run admission preserved the existing DeferOne occurrence and suppressed this additional exact occurrence.");
            case ScheduleRunAdmissionStoreStatus.Retired:
                return Result(GovernedLoopSequentialMaterializationStatus.Retired, null, anchor, "Atomic run admission authenticated a compacted terminal watermark for the exact occurrence; provider dispatch remains forbidden.");
            case ScheduleRunAdmissionStoreStatus.Conflict:
                return Result(GovernedLoopSequentialMaterializationStatus.Conflict, result.Run, anchor, "Atomic schedule run-admission evidence is bound to different immutable coordinates.");
            case ScheduleRunAdmissionStoreStatus.LimitExceeded:
                return Result(GovernedLoopSequentialMaterializationStatus.LimitExceeded, null, anchor, "A bounded run or schedule-admission evidence limit rejected materialization.");
            default:
                return Result(GovernedLoopSequentialMaterializationStatus.Unavailable, result.Run, anchor, $"Atomic schedule run admission returned unsupported status `{result.Status}`.");
        }
    }

    private async Task<GovernedLoopSequentialMaterializationResult> ReconcileAfterPossibleCreateAsync(
        GovernedLoopSequentialMaterializationRequest request,
        CustomLoopDefinition definition,
        GovernedLoopSequentialRunAnchor anchor,
        Exception exception)
    {
        using var integrityWindow = new CancellationTokenSource(_integrityWriteTimeout);
        ExistingRead read;
        try
        {
            read = await ReadExistingAsync(request, integrityWindow.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Result(GovernedLoopSequentialMaterializationStatus.Unavailable, null, anchor, $"Run creation returned {exception.GetType().Name}, and bounded reconciliation timed out before exact durability could be proved.");
        }

        if (read.Status is not null)
        {
            return Result(read.Status.Value, read.Run, anchor, read.Detail!);
        }

        if (read.Run is null)
        {
            return Result(GovernedLoopSequentialMaterializationStatus.Unavailable, null, anchor, $"Run creation may have failed before a durable commit and reconciliation found no exact run: {exception.GetType().Name}.");
        }

        return await ReconcileExistingAsync(request, definition, anchor, read.Run, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<GovernedLoopSequentialMaterializationResult> ReconcileExistingAsync(
        GovernedLoopSequentialMaterializationRequest request,
        CustomLoopDefinition definition,
        GovernedLoopSequentialRunAnchor anchor,
        CustomLoopRunRecord run,
        CancellationToken cancellationToken)
    {
        if (request.InvocationSnapshot.TriggerOrigin is not null)
        {
            ScheduleRunAdmissionStoreResult scheduled;
            try
            {
                scheduled = await CreateScheduledAsync(request, CreateRun(request, definition), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                return Result(
                    GovernedLoopSequentialMaterializationStatus.Unavailable,
                    run,
                    anchor,
                    $"Atomic schedule run-admission evidence could not be reconciled before audit: {exception.GetType().Name}.");
            }

            var scheduledResult = await ResolveScheduledResultAsync(request, definition, anchor, scheduled).ConfigureAwait(false);
            if (scheduledResult is not null)
            {
                return scheduledResult;
            }

            run = scheduled.Run!;
        }

        return await ReconcileMaterializedRunAsync(request, definition, anchor, run, cancellationToken).ConfigureAwait(false);
    }

    private async Task<GovernedLoopSequentialMaterializationResult> ReconcileMaterializedRunAsync(
        GovernedLoopSequentialMaterializationRequest request,
        CustomLoopDefinition definition,
        GovernedLoopSequentialRunAnchor anchor,
        CustomLoopRunRecord run,
        CancellationToken cancellationToken)
    {
        if (!MatchesExpectedAdmission(run, request, definition))
        {
            return Result(GovernedLoopSequentialMaterializationStatus.Conflict, run, anchor, "Existing durable run evidence does not match the exact committed canonical admission.");
        }

        if (CustomLoopRunValidator.HasCompleteAdmissionAudit(run))
        {
            return Result(GovernedLoopSequentialMaterializationStatus.Replayed, run, anchor, "The exact canonical run and its admission-audit boundary were already durable; no materialization was repeated.");
        }

        if (run.Status != CustomLoopRunStatus.Admitted || run.LifecycleVersion != 1 || run.Events.Length != 1)
        {
            return Result(GovernedLoopSequentialMaterializationStatus.AuditUnavailable, run, anchor, "Existing run evidence lacks the required admission-audit prefix and cannot be repaired automatically from its current lifecycle state.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return await CompleteAuditBoundaryAsync(request, definition, anchor, run, replay: true).ConfigureAwait(false);
    }

    private async Task<GovernedLoopSequentialMaterializationResult> CompleteAuditBoundaryAsync(
        GovernedLoopSequentialMaterializationRequest request,
        CustomLoopDefinition definition,
        GovernedLoopSequentialRunAnchor anchor,
        CustomLoopRunRecord run,
        bool replay)
    {
        try
        {
            using var auditWindow = new CancellationTokenSource(_integrityWriteTimeout);
            var audit = await _auditRecorder.RecordOnceAsync(
                GovernedLoopSequentialAuditOperationId.ForAdmission(
                    request.AdmissionReceipt.ContentHash,
                    request.AdapterBinding.ContentHash),
                request.AdapterBinding.ContentHash,
                CreateAdmissionAudit(request, definition, run),
                auditWindow.Token).ConfigureAwait(false);
            if (audit.Status == GovernedLoopSequentialAuditRecordStatus.Conflict)
            {
                return Result(GovernedLoopSequentialMaterializationStatus.AuditConflict, run, anchor, "The stable canonical admission-audit operation is durably bound to different evidence; execution remains forbidden.");
            }

            if (audit.Status == GovernedLoopSequentialAuditRecordStatus.Unavailable)
            {
                return Result(GovernedLoopSequentialMaterializationStatus.AuditUnavailable, run, anchor, "The canonical admission audit recorder could not prove a durable result; execution remains forbidden.");
            }

            if (audit.Status is not (GovernedLoopSequentialAuditRecordStatus.Recorded or GovernedLoopSequentialAuditRecordStatus.AlreadyRecorded))
            {
                return Result(GovernedLoopSequentialMaterializationStatus.AuditUnavailable, run, anchor, $"The canonical admission audit recorder returned unsupported status `{audit.Status}`; execution remains forbidden.");
            }
        }
        catch (Exception exception)
        {
            return Result(GovernedLoopSequentialMaterializationStatus.AuditUnavailable, run, anchor, $"The canonical admission audit could not be proved durable: {exception.GetType().Name}.");
        }

        var now = UtcNow(run.UpdatedAtUtc);
        var marker = new CustomLoopRunEvent(
            2,
            _eventIdentityGenerator.NewEventId(),
            now,
            CustomLoopRunEventKind.AdmissionAuditCompleted,
            null,
            null,
            null,
            AuditMarkerDetail,
            [],
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null);
        var candidate = run with
        {
            LifecycleVersion = 2,
            UpdatedAtUtc = now,
            Events = [run.Events[0], marker],
        };
        if (!CustomLoopRunValidator.ValidateUpdate(run, candidate).IsValid)
        {
            return Result(GovernedLoopSequentialMaterializationStatus.Invalid, run, anchor, "The admission-audit marker did not form an exact valid successor.");
        }

        try
        {
            using var markerWindow = new CancellationTokenSource(_integrityWriteTimeout);
            var stored = await _runStore.UpdateAsync(candidate, run.LifecycleVersion, markerWindow.Token).ConfigureAwait(false);
            if (stored.Status == CustomLoopRunStoreStatus.Updated
                && stored.Run is not null
                && MatchesExpectedAdmission(stored.Run, request, definition)
                && CustomLoopRunValidator.HasCompleteAdmissionAudit(stored.Run))
            {
                return Result(
                    replay ? GovernedLoopSequentialMaterializationStatus.Replayed : GovernedLoopSequentialMaterializationStatus.Ready,
                    stored.Run,
                    anchor,
                    replay
                        ? "The prior exact run's canonical admission audit was reconciled before execution."
                        : "The exact canonical run and admission-audit boundary are durable before execution.");
            }
        }
        catch
        {
            // The update may have committed before the adapter failed. Reconcile below; never infer durability from the exception.
        }

        using var reconciliationWindow = new CancellationTokenSource(_integrityWriteTimeout);
        ExistingRead reconciled;
        try
        {
            reconciled = await ReadExistingAsync(request, reconciliationWindow.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Result(GovernedLoopSequentialMaterializationStatus.AuditUnavailable, run, anchor, "The admission-audit marker write was uncertain, and bounded reconciliation timed out; execution remains forbidden.");
        }

        if (reconciled.Status is null
            && reconciled.Run is not null
            && MatchesExpectedAdmission(reconciled.Run, request, definition)
            && CustomLoopRunValidator.HasCompleteAdmissionAudit(reconciled.Run))
        {
            return Result(GovernedLoopSequentialMaterializationStatus.Replayed, reconciled.Run, anchor, "The admission-audit marker committed before an uncertain store response and was authenticated by exact replay.");
        }

        return Result(GovernedLoopSequentialMaterializationStatus.AuditUnavailable, reconciled.Run ?? run, anchor, "The append-only admission audit may be durable, but the ordered run has no authenticated completion marker; execution is forbidden until reconciliation.");
    }

    private async Task<ExistingRead> ReadExistingAsync(
        GovernedLoopSequentialMaterializationRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var byOperation = await _runStore.GetByAdmissionOperationAsync(request.AdmissionRequest.OperationId, cancellationToken).ConfigureAwait(false);
            var byRun = await _runStore.GetAsync(request.AdmissionReceipt.Evidence.Binding.RunId, cancellationToken).ConfigureAwait(false);
            if (byOperation is not null && byRun is not null && !string.Equals(byOperation.Id, byRun.Id, StringComparison.Ordinal))
            {
                return new ExistingRead(GovernedLoopSequentialMaterializationStatus.Conflict, byOperation, "Admission-operation and receipt-run indexes resolve to different durable runs.");
            }

            return new ExistingRead(null, byOperation ?? byRun, null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new ExistingRead(GovernedLoopSequentialMaterializationStatus.Unavailable, null, $"Existing run evidence could not be read safely: {exception.GetType().Name}.");
        }
    }

    private CustomLoopRunRecord CreateRun(
        GovernedLoopSequentialMaterializationRequest request,
        CustomLoopDefinition definition)
    {
        var receipt = request.AdmissionReceipt;
        var binding = request.AdapterBinding;
        var invocation = request.InvocationSnapshot;
        var now = receipt.RecordedAtUtc.ToUniversalTime();
        var admitted = new CustomLoopRunEvent(
            1,
            _eventIdentityGenerator.NewEventId(),
            now,
            CustomLoopRunEventKind.Admitted,
            null,
            null,
            null,
            AdmissionDetail,
            [],
            null,
            null,
            null,
            null,
            null,
            null,
            invocation.ModelSnapshot.Provider,
            invocation.ModelSnapshot.Model,
            null,
            null);
        var triggerEvidence = CustomLoopSequentialNodeEvidenceHash.Apply(new CustomLoopSequentialNodeEvidence(
            CustomLoopSequentialNodeEvidence.CurrentSchemaVersion,
            CustomLoopSequentialNodeEvidenceKind.CompletedOutcome,
            binding.WorkspaceId,
            binding.ExecutionBinding.RunId,
            binding.ExecutionBinding.Revision,
            binding.ExecutionBinding.ExecutionGeneration,
            0,
            1,
            request.Plan.Nodes[0].NodeId,
            1,
            null,
            null,
            GovernedLoopControlCondition.Always,
            request.Plan.ControlEdges.Where(edge => string.Equals(edge.FromNodeId, request.Plan.Nodes[0].NodeId, StringComparison.Ordinal) && edge.Condition == GovernedLoopControlCondition.Always).Select(edge => edge.Id).Order(StringComparer.Ordinal).ToArray(),
            request.Plan.ControlEdges.Where(edge => string.Equals(edge.FromNodeId, request.Plan.Nodes[0].NodeId, StringComparison.Ordinal) && edge.Condition != GovernedLoopControlCondition.Always).Select(edge => edge.Id).Order(StringComparer.Ordinal).ToArray(),
            null,
            null,
            CustomLoopSequentialNodeDisposition.Completed,
            CustomLoopSequentialOutcomeArtifactHash.Compute(admitted),
            string.Empty));
        admitted = admitted with { SequentialNodeEvidence = triggerEvidence };
        var context = CustomLoopContextSnapshotHash.Apply(new CustomLoopContextSnapshot(
            CustomLoopContextSnapshot.CurrentSchemaVersion,
            invocation.ContextCapturedAtUtc,
            invocation.ContextManifest.ToArray(),
            string.Empty));
        var run = new CustomLoopRunRecord(
            CustomLoopRunRecord.CurrentSchemaVersion,
            receipt.Evidence.Binding.RunId,
            request.Artifact.Graph.GraphId,
            1,
            CustomLoopRunStatus.Admitted,
            now,
            now,
            null,
            request.AdmissionRequest.Surface,
            invocation.ModelSnapshot,
            request.AdmissionRequest.OperationId,
            request.AdmissionRequest.ActorId.Value,
            string.Empty,
            definition,
            invocation.TriggerPrompt,
            invocation.InvokingConversation,
            context,
            CustomLoopExecutionClock.NotStarted(),
            CustomLoopRunCheckpoint.Start(),
            [admitted],
            null,
            null,
            null)
        {
            CapabilityAdmission = receipt.Evidence.CapabilityAdmission,
            SequentialInvocationSnapshot = invocation,
            SequentialAdapterBinding = binding,
            Frontier = AssertInitialFrontier(request, admitted, triggerEvidence, now),
        };
        return CustomLoopAdmissionRequestHash.Apply(run);
    }

    private static GovernedLoopFrontierPosture AssertInitialFrontier(
        GovernedLoopSequentialMaterializationRequest request,
        CustomLoopRunEvent admitted,
        CustomLoopSequentialNodeEvidence triggerEvidence,
        DateTimeOffset now)
    {
        var initialized = GovernedLoopSequentialFrontierMachine.Initialize(
            request.AdapterBinding,
            request.Plan,
            admitted.EventId,
            admitted.EventId,
            triggerEvidence.OutcomeArtifactHash,
            now);
        return initialized.Status == GovernedLoopSequentialFrontierTransitionStatus.Applied && initialized.Frontier is not null
            ? initialized.Frontier
            : throw new InvalidOperationException(initialized.Detail);
    }

    private static bool MatchesExpectedAdmission(
        CustomLoopRunRecord? run,
        GovernedLoopSequentialMaterializationRequest request,
        CustomLoopDefinition definition)
    {
        if (run is null || !CustomLoopRunValidator.Validate(run).IsValid || run.Events.Length == 0)
        {
            return false;
        }

        var receipt = request.AdmissionReceipt;
        var invocation = request.InvocationSnapshot;
        var initial = run.Events[0];
        var trigger = initial.SequentialNodeEvidence;
        return string.Equals(run.Id, receipt.Evidence.Binding.RunId, StringComparison.Ordinal)
            && string.Equals(run.LoopId, request.Artifact.Graph.GraphId, StringComparison.Ordinal)
            && string.Equals(run.AdmissionOperationId, request.AdmissionRequest.OperationId, StringComparison.Ordinal)
            && string.Equals(run.AdmissionActor, request.AdmissionRequest.ActorId.Value, StringComparison.Ordinal)
            && string.Equals(run.Surface, request.AdmissionRequest.Surface, StringComparison.Ordinal)
            && run.CreatedAtUtc == receipt.RecordedAtUtc
            && Equals(run.ModelSnapshot, invocation.ModelSnapshot)
            && Equals(run.InvokingConversation, invocation.InvokingConversation)
            && string.Equals(run.TriggerPrompt, invocation.TriggerPrompt, StringComparison.Ordinal)
            && string.Equals(run.AdmittedDefinition.ContentHash, definition.ContentHash, StringComparison.Ordinal)
            && string.Equals(run.SequentialInvocationSnapshot?.ContentHash, invocation.ContentHash, StringComparison.Ordinal)
            && string.Equals(run.SequentialAdapterBinding?.ContentHash, request.AdapterBinding.ContentHash, StringComparison.Ordinal)
            && GovernedLoopSequentialFrontierMachine.Validate(run.Frontier, request.AdapterBinding, request.Plan)
            && string.Equals(
                GovernedLoopAdmissionContractHash.ComputeCapabilityAdmissionReferenceHash(run.CapabilityAdmission),
                GovernedLoopAdmissionContractHash.ComputeCapabilityAdmissionReferenceHash(receipt.Evidence.CapabilityAdmission),
                StringComparison.Ordinal)
            && run.ContextSnapshot.CapturedAtUtc == invocation.ContextCapturedAtUtc
            && run.ContextSnapshot.SourceManifest.SequenceEqual(invocation.ContextManifest)
            && initial.Sequence == 1
            && initial.Kind == CustomLoopRunEventKind.Admitted
            && initial.TimestampUtc == receipt.RecordedAtUtc
            && string.Equals(initial.Detail, AdmissionDetail, StringComparison.Ordinal)
            && string.Equals(initial.Provider, invocation.ModelSnapshot.Provider, StringComparison.Ordinal)
            && string.Equals(initial.Model, invocation.ModelSnapshot.Model, StringComparison.Ordinal)
            && trigger is
            {
                Kind: CustomLoopSequentialNodeEvidenceKind.CompletedOutcome,
                Disposition: CustomLoopSequentialNodeDisposition.Completed,
                ActivationOrdinal: 0,
                VisitOrdinal: 1,
                Attempt: 1,
            }
            && string.Equals(trigger.NodeId, request.Plan.Nodes[0].NodeId, StringComparison.Ordinal)
            && trigger.ControlOutcome == GovernedLoopControlCondition.Always
            && trigger.SelectedControlEdgeIds.SequenceEqual(run.Frontier?.Payload.Nodes[0].SelectedControlEdgeIds ?? [], StringComparer.Ordinal)
            && trigger.SkippedControlEdgeIds.SequenceEqual(run.Frontier?.Payload.Nodes[0].SkippedControlEdgeIds ?? [], StringComparer.Ordinal)
            && string.Equals(run.Frontier?.Payload.Nodes[0].AttemptOperationId, initial.EventId, StringComparison.Ordinal)
            && string.Equals(run.Frontier?.Payload.Nodes[0].OutcomeEvidenceId, initial.EventId, StringComparison.Ordinal)
            && string.Equals(run.Frontier?.Payload.Nodes[0].OutcomeEvidenceHash, trigger.OutcomeArtifactHash, StringComparison.Ordinal)
            && CustomLoopSequentialNodeEvidenceHash.Matches(trigger)
            && CustomLoopSequentialOutcomeArtifactHash.Matches(initial)
            && CustomLoopAdmissionRequestHash.Matches(run);
    }

    private AuditEvent CreateAdmissionAudit(
        GovernedLoopSequentialMaterializationRequest request,
        CustomLoopDefinition definition,
        CustomLoopRunRecord run)
    {
        var timestamp = request.AdmissionReceipt.RecordedAtUtc.ToUniversalTime();
        return new AuditEvent(
            timestamp,
            request.AdmissionRequest.ActorId.Value,
            AuditSchema.Actions.LoopRunAdmission,
            run.Id,
            AuditSchema.Outcomes.Succeeded,
            "Canonical sequential governed-loop admission materialized before ordered execution.",
            new Dictionary<string, object?>
            {
                ["admission_status"] = "admitted",
                ["run_id"] = run.Id,
                ["loop_id"] = run.LoopId,
                ["operation_id"] = request.AdmissionRequest.OperationId,
                ["definition_hash"] = definition.ContentHash,
                ["graph_artifact_hash"] = request.AdapterBinding.GraphArtifactHash,
                ["graph_layout_hash"] = request.AdapterBinding.GraphLayoutHash,
                ["admission_request_hash"] = request.AdapterBinding.AdmissionRequestHash,
                ["admission_receipt_hash"] = request.AdapterBinding.AdmissionReceiptHash,
                ["adapter_binding_hash"] = request.AdapterBinding.ContentHash,
                ["surface"] = request.AdmissionRequest.Surface,
            });
    }

    private DateTimeOffset UtcNow(DateTimeOffset minimum)
    {
        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        return now < minimum ? minimum : now;
    }

    private static GovernedLoopSequentialMaterializationResult Result(
        GovernedLoopSequentialMaterializationStatus status,
        CustomLoopRunRecord? run,
        GovernedLoopSequentialRunAnchor? anchor,
        string detail)
        => new(status, run, anchor, detail);

    private sealed record ExistingRead(
        GovernedLoopSequentialMaterializationStatus? Status,
        CustomLoopRunRecord? Run,
        string? Detail);
}
