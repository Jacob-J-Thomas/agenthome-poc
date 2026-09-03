using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Loops.Admission;
using EmbodySense.Core.Application.Loops.Admission.Models;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Execution.Effects.Models;
using EmbodySense.Core.Application.Loops.EffectAttempts.Models;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.LocalWorkspace.Actions;
using EmbodySense.Core.Common.Loops.Admission;
using EmbodySense.Core.Common.Loops.Admission.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Effects;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;
using EmbodySense.Core.Common.Loops.Failures;
using EmbodySense.Core.Common.Loops.Failures.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Sequential;
using EmbodySense.Core.Common.Loops.Sequential.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Persistence.Loops.Admission;
using EmbodySense.Core.Persistence.Loops.EffectAttempts;
using EmbodySense.Core.Persistence.Loops.Execution.Reconciliation;
using EmbodySense.Core.Persistence.Permissions;
using EmbodySense.Core.Startup.Loops.Execution.Effects;

namespace EmbodySense.E2ETests.Web;

internal static partial class HumanReviewBrowserFixture
{
    internal static async Task<EffectReconciliationBrowserSeed> SeedEffectReconciliationAsync(
        WorkspacePaths paths,
        string runId,
        string prompt,
        string capabilityTrustRoot,
        bool retainAppliedOutcome = true)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityTrustRoot);

        var now = DateTimeOffset.UtcNow.AddMinutes(-5);
        var workspaceId = CapabilityWorkspaceScopeId.Create(paths.RootPath);
        var role = CreateBrowserHumanReviewRole(workspaceId, now, includePreDispatchEffect: true);
        var artifact = CreateArtifact(runId, now, new ContextualRoleRevisionPin(role.Identity, role.ContentHash), includePreDispatchEffect: true);
        var planResult = GovernedLoopSequentialPlanBuilder.Build(artifact);
        var plan = planResult.Plan ?? throw new InvalidOperationException($"The browser Effect Reconciliation fixture graph was not plannable: {planResult.Status}.");
        var context = CustomLoopContextSnapshot.CreateEmpty(now);
        var invocation = GovernedLoopSequentialContractHash.Apply(new GovernedLoopSequentialInvocationSnapshot(
            GovernedLoopSequentialInvocationSnapshot.CurrentSchemaVersion,
            prompt,
            new CustomLoopModelSnapshot("provider", "model"),
            null,
            context.CapturedAtUtc,
            context.SourceManifest,
            string.Empty));
        var publication = GovernedLoopRevisionPublicationPinFactory.Create(1, artifact.RevisionArtifact.Revision, "browser-effect-reconciliation-publish-" + runId, Hash('7'));
        var execution = GovernedLoopExecutionBinding.Create(1, runId, publication.Revision, 1);
        if (!AuthorityActorId.TryParse("user-owner", out var actorId, out _))
        {
            throw new InvalidOperationException("The browser Effect Reconciliation fixture authority identity is invalid.");
        }

        var authority = await SeedCanonicalAuthorityDependenciesAsync(paths, capabilityTrustRoot, artifact, publication, role, workspaceId, runId, now, includePreDispatchEffect: true).ConfigureAwait(false);
        var admissionOperationId = "browser-effect-reconciliation-admit-" + runId;
        var admissionRequest = GovernedLoopAdmissionRequestHash.Apply(new GovernedLoopAdmissionRequest(
            GovernedLoopAdmissionRequest.CurrentSchemaVersion,
            admissionOperationId,
            invocation.ContentHash,
            string.Empty,
            publication,
            authority.GrantReference,
            actorId!,
            "web"));
        var intent = new GovernedLoopAdmissionIntent(
            GovernedLoopAdmissionIntent.CurrentSchemaVersion,
            workspaceId,
            admissionRequest.OperationId,
            admissionRequest.RequestHash,
            publication,
            authority.GrantReference,
            artifact.Graph.OwningRole,
            actorId!,
            admissionRequest.Surface,
            artifact.ArtifactHash,
            artifact.LayoutHash);
        var admissionReceipt = CreateAdmissionReceipt(artifact, execution, intent, workspaceId, now, authority.GrantProfile, authority.GrantBoundary, authority.DependencyEvidenceHash, authority.EffectiveAuthority);
        var admissionOutcome = GovernedLoopAdmissionContractHash.Apply(new GovernedLoopAdmissionTerminalOutcome(
            GovernedLoopAdmissionTerminalOutcome.CurrentSchemaVersion,
            intent,
            GovernedLoopAdmissionDisposition.Admitted,
            admissionReceipt,
            null,
            now,
            string.Empty));
        var admissionValidation = GovernedLoopAdmissionValidator.Validate(admissionOutcome);
        if (!admissionValidation.IsValid)
        {
            throw new InvalidOperationException("The browser Effect Reconciliation fixture admission outcome is invalid: " + string.Join(',', admissionValidation.Errors));
        }

        var canonicalAdmissionStore = new GovernedLoopAdmissionStore(paths, new FileCapabilityCatalogTrustProvider(capabilityTrustRoot));
        var admissionRead = await canonicalAdmissionStore.ReadByOperationAsync(workspaceId, admissionRequest.OperationId).ConfigureAwait(false);
        if (admissionRead.Status != GovernedLoopAdmissionStoreReadStatus.NotFound || admissionRead.Outcome is not null)
        {
            throw new InvalidOperationException($"The browser Effect Reconciliation fixture admission identity was not empty: {admissionRead.Status}.");
        }

        var admissionCommit = await canonicalAdmissionStore.CommitAsync(new GovernedLoopAdmissionStoreMutation(
            workspaceId,
            admissionRequest.OperationId,
            admissionRequest.RequestHash,
            GovernedLoopAdmissionContractHash.ComputeIntentHash(intent),
            admissionRead.StoreGeneration,
            admissionOutcome)).ConfigureAwait(false);
        if (admissionCommit.Status != GovernedLoopAdmissionStoreCommitStatus.Committed)
        {
            throw new InvalidOperationException($"The browser Effect Reconciliation fixture admission outcome was not committed: {admissionCommit.Status}.");
        }

        var adapterBinding = GovernedLoopSequentialContractHash.Apply(new GovernedLoopSequentialAdapterBinding(
            GovernedLoopSequentialAdapterBinding.CurrentSchemaVersion,
            workspaceId,
            execution,
            admissionRequest.OperationId,
            admissionReceipt,
            admissionReceipt.ContentHash,
            admissionRequest.RequestHash,
            invocation.ContentHash,
            artifact.ArtifactHash,
            artifact.LayoutHash,
            [],
            string.Empty));
        using var store = new CustomLoopRunStore(paths);
        var materialized = await new GovernedLoopSequentialRunMaterializer(
            store,
            new BrowserAuditRecorder(),
            new GovernedLoopSequentialEventIdentityGenerator(),
            new BrowserTimeProvider(now)).MaterializeAsync(new GovernedLoopSequentialMaterializationRequest(
                GovernedLoopSequentialMaterializationRequest.CurrentSchemaVersion,
                admissionRequest,
                admissionReceipt,
                artifact,
                plan,
                invocation,
                adapterBinding)).ConfigureAwait(false);
        var admitted = materialized.Run ?? throw new InvalidOperationException($"The browser Effect Reconciliation fixture run was not materialized: {materialized.Status} {materialized.Detail}");
        var running = TransitionToRunning(admitted);
        RequireEffectReconciliationUpdate(await store.UpdateAsync(running, admitted.LifecycleVersion).ConfigureAwait(false), "running lifecycle");
        var started = ClaimReviewTarget(running, plan, adapterBinding, includePreDispatchEffect: true);
        RequireEffectReconciliationUpdate(await store.UpdateAsync(started, running.LifecycleVersion).ConfigureAwait(false), "started Action");

        var activation = started.Frontier?.Payload.Nodes.SingleOrDefault(item => item.Status == GovernedLoopNodeExecutionStatus.Running)
            ?? throw new InvalidOperationException("The browser Effect Reconciliation fixture has no exact running Action activation.");
        var prepared = await CreateEffectReconciliationAttemptAsync(started, activation, paths).ConfigureAwait(false);
        if (retainAppliedOutcome)
        {
            await RetainAppliedWorkspaceOutcomeAsync(paths, prepared).ConfigureAwait(false);
        }
        var attempt = await PersistReconciliationRequiredAttemptAsync(paths, prepared).ConfigureAwait(false);
        var binding = GovernedLoopEffectReconciliationContract.CreateBinding(workspaceId, activation.ActivationOrdinal, activation.VisitOrdinal, attempt);
        var terminal = await PersistEffectReconciliationBlockedRunAsync(store, started, adapterBinding, activation, binding).ConfigureAwait(false);
        await PersistCanonicalOpenEffectReconciliationCaseAsync(paths, terminal, attempt, binding).ConfigureAwait(false);

        var markerPath = Path.Combine(paths.RootPath, "shared", "process-observable-marker.txt");
        var markerContent = retainAppliedOutcome
            ? await File.ReadAllTextAsync(markerPath).ConfigureAwait(false)
            : string.Empty;
        return new EffectReconciliationBrowserSeed(runId, "case-effect-reconciliation-" + binding.ContentHash, attempt, binding, markerPath, markerContent);
    }

    internal static async Task SeedAuthoritativeNotAppliedObservationAsync(WorkspacePaths paths, EffectReconciliationBrowserSeed seeded)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(seeded);
        var effects = new GovernedLoopEffectAttemptStore(paths);
        var cases = new GovernedLoopEffectReconciliationCaseStore(effects);
        var page = await cases.ListAsync(new GovernedLoopEffectReconciliationCaseListRequest(100)).ConfigureAwait(false);
        var summary = page.Cases.Single(item => string.Equals(item.CaseId, seeded.CaseId, StringComparison.Ordinal));
        var reference = new GovernedLoopEffectReconciliationCaseReference(summary.CaseId, summary.CaseVersion, summary.ContentHash, summary.BindingHash);
        var caseRead = await cases.ReadAsync(new GovernedLoopEffectReconciliationCaseReadRequest(reference)).ConfigureAwait(false);
        var value = caseRead.Case ?? throw new InvalidOperationException("The browser Effect Reconciliation not-applied seed case was unavailable.");
        var effectRead = await effects.ReadAsync(seeded.Binding.WorkspaceId, seeded.Binding.OperationId, seeded.Binding.EffectGeneration).ConfigureAwait(false);
        var effect = effectRead.Attempt ?? throw new InvalidOperationException("The browser Effect Reconciliation not-applied seed effect was unavailable.");
        var source = value.EvidenceSources.Single();
        var context = new GovernedLoopEffectReconciliationProbeReservationContext(
            reference,
            value.Binding,
            value.ContractMetadata,
            effect,
            source,
            new GovernedLoopEffectReconciliationProbeTarget(effect.TargetFingerprint, effect.PreconditionEvidenceHash, effect.BeforeEvidenceId),
            effect.InputFingerprint);
        var operationId = "fixture-not-applied-probe-" + seeded.Binding.ContentHash;
        var reservation = await cases.ReserveAsync(new GovernedLoopEffectReconciliationProbeReservationRequest(
            operationId,
            EffectReconciliationProbeRequestHash(operationId, context),
            context)).ConfigureAwait(false);
        if (reservation.Status != GovernedLoopEffectReconciliationProbeReservationStatus.Reserved || reservation.Reservation is null)
        {
            throw new InvalidOperationException($"The browser Effect Reconciliation not-applied probe was not reserved: {reservation.Status}.");
        }

        var observation = GovernedLoopEffectReconciliationContractHash.Apply(new GovernedLoopEffectReconciliationObservation(
            GovernedLoopEffectReconciliationContractLimits.CurrentSchemaVersion,
            value.CaseId,
            value.Binding.ContentHash,
            "fixture-not-applied-observation",
            source.SourceId,
            source.ContentHash,
            GovernedLoopEffectReconciliationObservationKind.Evidence,
            source.ReliabilityPosture,
            GovernedLoopEffectReconciliationObservedOutcome.NotApplied,
            "fixture-not-applied-evidence",
            Hash('d'),
            value.OpenedAtUtc,
            value.OpenedAtUtc,
            "The server-owned fixture established that no matching external effect exists.",
            string.Empty));
        var committed = await cases.CommitObservationAsync(new GovernedLoopEffectReconciliationProbeObservationCommitRequest(
            reservation.Reservation,
            new GovernedLoopEffectReconciliationProbeInvocationResult(GovernedLoopEffectReconciliationProbeInvocationStatus.Ready, observation))).ConfigureAwait(false);
        if (committed.Status != GovernedLoopEffectReconciliationProbeReservationStatus.Reserved || committed.Case is null)
        {
            throw new InvalidOperationException($"The browser Effect Reconciliation not-applied observation was not committed: {committed.Status}.");
        }
    }

    private static async Task RetainAppliedWorkspaceOutcomeAsync(WorkspacePaths paths, GovernedLoopEffectAttempt prepared)
    {
        var permissionPolicy = new PermissionPolicyStore().Load(paths);
        var registry = GovernedWorkspaceActionFactory.CreateRegistry(paths, new CapabilityAuthorityTransaction(paths), new EmbodySense.Core.Application.Governance.Tools.ToolPermissionService(paths, permissionPolicy));
        var descriptor = registry.Descriptors.Single(item => string.Equals(item.OperationId, WorkspaceActionOperationIds.Write, StringComparison.Ordinal));
        if (!registry.TryResolve(descriptor, out var operation)
            || operation is null
            || !GovernedActuatorInputContract.TryCanonicalize(PreDispatchEffectInput, out var input, out _))
        {
            throw new InvalidOperationException("The browser Effect Reconciliation fixture workspace probe target could not be reconstructed.");
        }

        var result = await operation.ExecuteAsync(new GovernedActuatorInvocation(
            descriptor,
            prepared.Payload.EffectId,
            prepared.Payload.OperationId,
            prepared.Payload.EffectGeneration,
            input!,
            prepared.TargetFingerprint,
            prepared.PreconditionEvidenceHash,
            prepared.BeforeEvidenceId), BrowserImmediateGovernedActuatorDispatchBoundary.Instance).ConfigureAwait(false);
        if (result.Status != GovernedActuatorAdapterStatus.OutcomeObserved || result.Outcome?.Outcome != GovernedLoopEffectOutcome.Succeeded)
        {
            throw new InvalidOperationException($"The browser Effect Reconciliation fixture did not retain a conclusive applied outcome: {result.Status}.");
        }
    }

    private static async Task<GovernedLoopEffectAttempt> CreateEffectReconciliationAttemptAsync(
        CustomLoopRunRecord predecessor,
        GovernedLoopNodeExecutionEvidence activation,
        WorkspacePaths paths)
    {
        if (predecessor.SequentialAdapterBinding is not { } adapter
            || activation.Attempt is null
            || string.IsNullOrWhiteSpace(activation.AttemptOperationId)
            || !GovernedActuatorInputContract.TryCanonicalize(PreDispatchEffectInput, out var input, out _))
        {
            throw new InvalidOperationException("The browser Effect Reconciliation fixture effect identity is invalid.");
        }

        var permissionPolicy = new PermissionPolicyStore().Load(paths);
        var registry = GovernedWorkspaceActionFactory.CreateRegistry(paths, new CapabilityAuthorityTransaction(paths), new EmbodySense.Core.Application.Governance.Tools.ToolPermissionService(paths, permissionPolicy));
        var descriptor = registry.Descriptors.Single(item => string.Equals(item.OperationId, WorkspaceActionOperationIds.Write, StringComparison.Ordinal));
        if (!registry.TryResolve(descriptor, out var operation)
            || operation is null
            || await operation.PrepareAsync(input!).ConfigureAwait(false) is not { } preparation)
        {
            throw new InvalidOperationException("The browser Effect Reconciliation fixture workspace effect could not be prepared.");
        }

        var pin = adapter.AdmissionReceipt.Evidence.CapabilityAdmission.Pins.Single(item => string.Equals(item.DescriptorIdentity.Id.Value, WorkspaceCommandCapabilityId, StringComparison.Ordinal));
        if (!Equals(pin.DescriptorIdentity, descriptor.Capability)
            || !Equals(pin.Implementation, descriptor.Implementation))
        {
            throw new InvalidOperationException("The browser Effect Reconciliation fixture workspace effect pin is not exact.");
        }

        var effectIdentity = WorkspaceActionFingerprint.Compute(
            "embodysense.workspace-tool-effect.v1",
            adapter.ExecutionBinding.RunId,
            activation.NodeId,
            activation.Attempt.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            activation.AttemptOperationId,
            activation.AttemptOperationId,
            WorkspaceActionOperationIds.Write,
            PreDispatchEffectInput);
        return GovernedLoopEffectAttemptContract.Prepare(
            GovernedLoopExecutionBinding.Create(adapter.ExecutionBinding.SchemaVersion, adapter.ExecutionBinding.RunId, adapter.ExecutionBinding.Revision, adapter.ExecutionBinding.ExecutionGeneration),
            activation.NodeId,
            activation.Attempt.Value,
            pin.DescriptorIdentity,
            pin.Implementation,
            descriptor.OperationId,
            descriptor.ContentHash,
            "effect-" + effectIdentity,
            activation.AttemptOperationId,
            1,
            input!.Fingerprint,
            preparation.TargetFingerprint,
            preparation.PreconditionEvidenceHash,
            adapter.AdmissionReceipt.ContentHash,
            preparation.BeforeEvidenceId,
            predecessor.UpdatedAtUtc.AddSeconds(1));
    }

    private static async Task<GovernedLoopEffectAttempt> PersistReconciliationRequiredAttemptAsync(WorkspacePaths paths, GovernedLoopEffectAttempt prepared)
    {
        var authorized = GovernedLoopEffectAttemptContract.AttachDispatchAuthority(prepared, Hash('9'), prepared.Payload.UpdatedAtUtc.AddSeconds(1));
        var crossed = GovernedLoopEffectAttemptContract.Advance(authorized, GovernedLoopEffectPhase.DispatchBoundaryReached, GovernedLoopEffectOutcome.OutcomeUnknown, GovernedLoopEffectEvidenceStatus.Pending, null, null, authorized.Payload.UpdatedAtUtc.AddSeconds(1));
        var ambiguous = GovernedLoopEffectAttemptContract.Advance(crossed, GovernedLoopEffectPhase.ReconciliationRequired, GovernedLoopEffectOutcome.OutcomeUnknown, GovernedLoopEffectEvidenceStatus.Incomplete, null, null, crossed.Payload.UpdatedAtUtc.AddSeconds(1));
        var store = new GovernedLoopEffectAttemptStore(paths);
        var begun = await store.BeginAsync(prepared).ConfigureAwait(false);
        if (begun.Status != GovernedLoopEffectAttemptStoreStatus.Created || begun.Lease is null)
        {
            throw new InvalidOperationException($"The browser Effect Reconciliation fixture effect was not created: {begun.Status}.");
        }
        using var lease = begun.Lease;
        RequireEffectAttemptUpdate(await store.CompareExchangeAsync(prepared.ContentHash, authorized, lease).ConfigureAwait(false), "dispatch authority");
        RequireEffectAttemptUpdate(await store.CompareExchangeAsync(authorized.ContentHash, crossed, lease).ConfigureAwait(false), "dispatch boundary");
        RequireEffectAttemptUpdate(await store.CompareExchangeAsync(crossed.ContentHash, ambiguous, lease).ConfigureAwait(false), "reconciliation-required posture");
        return ambiguous;
    }

    private static async Task<CustomLoopRunRecord> PersistEffectReconciliationBlockedRunAsync(
        CustomLoopRunStore store,
        CustomLoopRunRecord started,
        GovernedLoopSequentialAdapterBinding adapter,
        GovernedLoopNodeExecutionEvidence activation,
        GovernedLoopEffectReconciliationBinding binding)
    {
        var dispatchAtUtc = started.UpdatedAtUtc.AddMinutes(1);
        var dispatch = new CustomLoopRunEvent(
            started.Events.Length + 1L,
            "event-effect-reconciliation-dispatch-" + started.Id,
            dispatchAtUtc,
            CustomLoopRunEventKind.NodeAttemptStarted,
            activation.CycleIteration ?? started.Checkpoint.Iteration,
            activation.NodeId,
            activation.Attempt,
            "Canonical workspace Action dispatch was retained before effect orchestration.",
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
            null,
            TraceReservationUtf8Bytes: EmbodySense.Core.Common.Loops.Custom.CustomLoopLimits.MaxGraphPureNodeOutcomeEvidenceReservationUtf8Bytes);
        dispatch = WithEffectReconciliationSequentialEvidence(dispatch, adapter, activation, CustomLoopSequentialNodeEvidenceKind.DispatchStarted, CustomLoopSequentialNodeDisposition.Unknown);
        var dispatchCandidate = started with
        {
            LifecycleVersion = started.LifecycleVersion + 1,
            UpdatedAtUtc = dispatchAtUtc,
            Events = [.. started.Events, dispatch],
        };
        var dispatchMutation = await store.UpdateAsync(dispatchCandidate, started.LifecycleVersion).ConfigureAwait(false);
        RequireEffectReconciliationUpdate(dispatchMutation, "dispatch evidence");
        var dispatched = dispatchMutation.Run ?? throw new InvalidOperationException("The browser Effect Reconciliation fixture dispatch evidence was not returned.");

        var ambiguityAtUtc = dispatchAtUtc.AddMinutes(1);
        var failureEvidence = GovernedLoopFailureEvidenceContract.Create(
            "failure-effect-reconciliation-" + started.Id,
            adapter.WorkspaceId,
            adapter.ExecutionBinding.RunId,
            adapter.ExecutionBinding.Revision,
            adapter.ExecutionBinding.ExecutionGeneration,
            activation.ActivationOrdinal,
            activation.VisitOrdinal,
            activation.NodeId,
            activation.Attempt!.Value,
            GovernedLoopFailureClass.AmbiguousExternalOutcome,
            "effect-reconciliation-outcome-ambiguous",
            GovernedLoopFailureSource.Actuator,
            GovernedLoopFailureEffectCertainty.Ambiguous,
            GovernedLoopFailureAuthorityPosture.NotApplicable,
            GovernedLoopFailureHumanPosture.None,
            GovernedLoopFailureRetrySafety.Unknown,
            GovernedLoopFailureSeverity.ReviewBlocked,
            990,
            [new GovernedLoopFailureEvidenceReference(dispatch.EventId, dispatch.SequentialNodeEvidence!.EvidenceHash)],
            null,
            ambiguityAtUtc);
        var ambiguity = new CustomLoopRunEvent(
            dispatched.Events.Length + 1L,
            "event-effect-reconciliation-ambiguous-" + started.Id,
            ambiguityAtUtc,
            CustomLoopRunEventKind.NodeAttemptFailed,
            activation.CycleIteration ?? dispatched.Checkpoint.Iteration,
            activation.NodeId,
            activation.Attempt,
            "The canonical workspace Action crossed its effect boundary without a conclusive runtime result; automatic redispatch is forbidden.",
            [],
            null,
            null,
            null,
            null,
            false,
            null,
            null,
            null,
            null,
            null)
        {
            FailureEvidence = failureEvidence,
            EffectReconciliationBinding = binding,
        };
        ambiguity = WithEffectReconciliationSequentialEvidence(ambiguity, adapter, activation, CustomLoopSequentialNodeEvidenceKind.AmbiguityAttention, CustomLoopSequentialNodeDisposition.NeedsReview);
        var ambiguityCandidate = dispatched with
        {
            LifecycleVersion = dispatched.LifecycleVersion + 1,
            UpdatedAtUtc = ambiguityAtUtc,
            Events = [.. dispatched.Events, ambiguity],
        };
        var ambiguityMutation = await store.UpdateAsync(ambiguityCandidate, dispatched.LifecycleVersion).ConfigureAwait(false);
        RequireEffectReconciliationUpdate(ambiguityMutation, "ambiguity evidence");
        var ambiguous = ambiguityMutation.Run ?? throw new InvalidOperationException("The browser Effect Reconciliation fixture ambiguity evidence was not returned.");

        var terminalAtUtc = ambiguityAtUtc.AddMinutes(1);
        var blocked = GovernedLoopSequentialFrontierMachine.ReviewBlockCurrent(ambiguous.Frontier, adapter, ambiguity.EventId, ambiguity.SequentialNodeEvidence!.OutcomeArtifactHash, null, [], [], terminalAtUtc);
        if (blocked.Status != GovernedLoopSequentialFrontierTransitionStatus.Applied || blocked.Frontier is null)
        {
            throw new InvalidOperationException($"The browser Effect Reconciliation fixture frontier was not review-blocked: {blocked.Status}. {blocked.Detail}");
        }
        var lifecycle = new CustomLoopRunEvent(
            ambiguous.Events.Length + 1L,
            "event-effect-reconciliation-needs-review-" + started.Id,
            terminalAtUtc,
            CustomLoopRunEventKind.LifecycleChanged,
            null,
            null,
            null,
            "The ambiguous effect requires explicit reconciliation.",
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
        var accumulated = ambiguous.ExecutionClock.AccumulatedRunningMilliseconds;
        if (ambiguous.ExecutionClock.ActiveSinceUtc is { } activeSince)
        {
            accumulated = checked(accumulated + Math.Max(0, (long)(terminalAtUtc - activeSince).TotalMilliseconds));
        }
        var terminal = ambiguous with
        {
            LifecycleVersion = ambiguous.LifecycleVersion + 1,
            Status = CustomLoopRunStatus.NeedsReview,
            UpdatedAtUtc = terminalAtUtc,
            CompletedAtUtc = terminalAtUtc,
            ExecutionClock = new CustomLoopExecutionClock(Math.Min(accumulated, EmbodySense.Core.Common.Loops.Custom.CustomLoopLimits.MaxRunExecutionMilliseconds), null),
            Events = [.. ambiguous.Events, lifecycle],
            FinalOutput = null,
            FailureCode = "workspace_action_reconciliation_required",
            FailureDetail = "The ambiguous effect requires explicit reconciliation.",
            Frontier = blocked.Frontier,
        };
        var terminalMutation = await store.UpdateAsync(terminal, ambiguous.LifecycleVersion).ConfigureAwait(false);
        RequireEffectReconciliationUpdate(terminalMutation, "terminal lifecycle");
        return terminalMutation.Run ?? throw new InvalidOperationException("The browser Effect Reconciliation fixture terminal run was not returned.");
    }

    private static async Task PersistCanonicalOpenEffectReconciliationCaseAsync(
        WorkspacePaths paths,
        CustomLoopRunRecord run,
        GovernedLoopEffectAttempt attempt,
        GovernedLoopEffectReconciliationBinding binding)
    {
        var permissionPolicy = new PermissionPolicyStore().Load(paths);
        var registry = GovernedWorkspaceActionFactory.CreateRegistry(paths, new CapabilityAuthorityTransaction(paths), new EmbodySense.Core.Application.Governance.Tools.ToolPermissionService(paths, permissionPolicy));
        var descriptor = registry.Descriptors.Single(item => string.Equals(item.OperationId, WorkspaceActionOperationIds.Write, StringComparison.Ordinal));
        var metadata = EffectReconciliationMetadata(descriptor);
        var ambiguity = run.Events.Single(item => Equals(item.EffectReconciliationBinding, binding));
        var frontier = run.Frontier ?? throw new InvalidOperationException("The browser Effect Reconciliation fixture terminal frontier was unavailable.");
        var caseId = "case-effect-reconciliation-" + binding.ContentHash;
        var source = GovernedLoopEffectReconciliationContractHash.Apply(new GovernedLoopEffectReconciliationEvidenceSource(
            GovernedLoopEffectReconciliationContractLimits.CurrentSchemaVersion,
            caseId,
            binding.ContentHash,
            "source-effect-reconciliation-" + metadata.ContentHash,
            GovernedLoopEffectReconciliationEvidenceSourceKind.Authoritative,
            GovernedLoopEffectReconciliationReliabilityPosture.Authoritative,
            metadata.ContractId,
            metadata.ContractVersion,
            metadata.ContentHash,
            EffectReconciliationAdmissionHash("source-registration", binding.ContentHash, metadata.ContentHash, run.SequentialAdapterBinding!.AdmissionReceiptHash, frontier.Payload.ContentHash, ambiguity.SequentialNodeEvidence!.EvidenceHash),
            run.UpdatedAtUtc,
            null,
            string.Empty));
        var receipts = new[]
        {
            run.SequentialAdapterBinding.AdmissionReceiptHash,
            frontier.Payload.ContentHash,
            ambiguity.SequentialNodeEvidence.EvidenceHash,
        }.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var value = GovernedLoopEffectReconciliationContract.Open(caseId, binding, metadata, [source], receipts, run.UpdatedAtUtc);
        var reference = new GovernedLoopEffectReconciliationCaseReference(value.CaseId, value.CaseVersion, value.ContentHash, binding.ContentHash);
        var operationId = "open-effect-reconciliation-" + binding.ContentHash;
        var semantic = new StringBuilder(4096);
        AppendEffectReconciliationHashValue(semantic, metadata.ContentHash);
        AppendEffectReconciliationHashValue(semantic, source.ContentHash);
        foreach (var receipt in receipts)
        {
            AppendEffectReconciliationHashValue(semantic, receipt);
        }
        var requestHash = EffectReconciliationOperationHash(operationId, "effect-reconciliation.open", reference, binding, semantic.ToString());
        var result = await new GovernedLoopEffectReconciliationCaseStore(new GovernedLoopEffectAttemptStore(paths)).CompareExchangeAsync(new GovernedLoopEffectReconciliationCaseMutationRequest(
            operationId,
            requestHash,
            "effect-reconciliation.open",
            null,
            null,
            binding,
            value,
            null)).ConfigureAwait(false);
        if (result.Status != GovernedLoopEffectReconciliationCaseMutationStatus.Applied || result.Case is null || result.EffectHead is null)
        {
            throw new InvalidOperationException($"The browser Effect Reconciliation fixture case was not opened atomically with its exact effect head: {result.Status}.");
        }
        if (!string.Equals(result.EffectHead.ContentHash, attempt.ContentHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The browser Effect Reconciliation fixture opened against an unexpected effect head.");
        }
    }

    private static GovernedLoopEffectReconciliationContractMetadata EffectReconciliationMetadata(GovernedActuatorOperationDescriptor descriptor)
    {
        var discriminator = descriptor.ContentHash[..32];
        var probeHash = EffectReconciliationActuatorHash("probe-contract", descriptor.ContentHash, descriptor.Capability.Hash.Value, descriptor.OperationId);
        return GovernedLoopEffectReconciliationContractHash.Apply(new GovernedLoopEffectReconciliationContractMetadata(
            GovernedLoopEffectReconciliationContractLimits.CurrentSchemaVersion,
            "actuator-reconciliation-" + discriminator,
            1,
            descriptor.Capability,
            descriptor.Implementation,
            descriptor.OperationId,
            descriptor.ContentHash,
            "actuator-outcome-probe-" + discriminator,
            1,
            probeHash,
            string.Empty));
    }

    private static string EffectReconciliationActuatorHash(string domain, params string[] values)
    {
        var builder = new StringBuilder("embodysense.actuator-reconciliation.v1\n").Append(domain).Append('\n');
        foreach (var value in values)
        {
            builder.Append(value.Length).Append(':').Append(value).Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    private static string EffectReconciliationAdmissionHash(string domain, params string[] values)
    {
        var builder = new StringBuilder("embodysense.reconciliation-attention-admission.v1\n").Append(domain).Append('\n');
        foreach (var value in values)
        {
            builder.Append(value.Length).Append(':').Append(value).Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    private static string EffectReconciliationOperationHash(
        string operationId,
        string purpose,
        GovernedLoopEffectReconciliationCaseReference reference,
        GovernedLoopEffectReconciliationBinding binding,
        string semanticFingerprint)
    {
        var builder = new StringBuilder(4096);
        AppendEffectReconciliationHashValue(builder, "embodysense.governed-loop-effect-reconciliation-operation.v1");
        AppendEffectReconciliationHashValue(builder, operationId);
        AppendEffectReconciliationHashValue(builder, purpose);
        AppendEffectReconciliationHashValue(builder, reference.CaseId);
        AppendEffectReconciliationHashValue(builder, reference.CaseVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AppendEffectReconciliationHashValue(builder, reference.BindingHash);
        AppendEffectReconciliationHashValue(builder, binding.ContentHash);
        AppendEffectReconciliationHashValue(builder, semanticFingerprint);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    private static string EffectReconciliationProbeRequestHash(string operationId, GovernedLoopEffectReconciliationProbeReservationContext context)
    {
        var builder = new StringBuilder(2048);
        AppendEffectReconciliationHashValue(builder, "embodysense.governed-loop-effect-reconciliation-probe.v1");
        AppendEffectReconciliationHashValue(builder, operationId);
        AppendEffectReconciliationHashValue(builder, context.Case.CaseId);
        AppendEffectReconciliationHashValue(builder, context.Case.CaseVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AppendEffectReconciliationHashValue(builder, context.Case.ContentHash);
        AppendEffectReconciliationHashValue(builder, context.Binding.ContentHash);
        AppendEffectReconciliationHashValue(builder, context.EffectHead.ContentHash);
        AppendEffectReconciliationHashValue(builder, context.InputFingerprint);
        AppendEffectReconciliationHashValue(builder, context.Target.TargetFingerprint);
        AppendEffectReconciliationHashValue(builder, context.Target.PreconditionEvidenceHash);
        AppendEffectReconciliationHashValue(builder, context.Target.BeforeEvidenceId);
        AppendEffectReconciliationHashValue(builder, context.Source.SourceId);
        AppendEffectReconciliationHashValue(builder, context.Source.ContentHash);
        AppendEffectReconciliationHashValue(builder, context.Source.RegistrationEvidenceHash);
        AppendEffectReconciliationHashValue(builder, context.Contract.ContentHash);
        AppendEffectReconciliationHashValue(builder, context.Contract.ProbeContractId);
        AppendEffectReconciliationHashValue(builder, context.Contract.ProbeContractVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AppendEffectReconciliationHashValue(builder, context.Contract.ProbeContractHash);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    private static void AppendEffectReconciliationHashValue(StringBuilder builder, string? value)
    {
        if (value is null)
        {
            builder.Append("-1:");
            return;
        }
        builder.Append(Encoding.UTF8.GetByteCount(value).ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.Append(':');
        builder.Append(value);
    }

    private static CustomLoopRunEvent WithEffectReconciliationSequentialEvidence(
        CustomLoopRunEvent runEvent,
        GovernedLoopSequentialAdapterBinding adapter,
        GovernedLoopNodeExecutionEvidence activation,
        CustomLoopSequentialNodeEvidenceKind kind,
        CustomLoopSequentialNodeDisposition disposition)
    {
        var evidence = CustomLoopSequentialNodeEvidenceHash.Apply(new CustomLoopSequentialNodeEvidence(
            CustomLoopSequentialNodeEvidence.CurrentSchemaVersion,
            kind,
            adapter.WorkspaceId,
            adapter.ExecutionBinding.RunId,
            adapter.ExecutionBinding.Revision,
            adapter.ExecutionBinding.ExecutionGeneration,
            activation.ActivationOrdinal,
            activation.VisitOrdinal,
            activation.NodeId,
            activation.Attempt!.Value,
            activation.CycleId,
            activation.CycleIteration,
            null,
            [],
            [],
            null,
            null,
            disposition,
            CustomLoopSequentialOutcomeArtifactHash.Compute(runEvent),
            string.Empty)
        {
            FailureEvidenceId = runEvent.FailureEvidence?.EvidenceId,
            FailureEvidenceHash = runEvent.FailureEvidence?.ContentHash,
        });
        return runEvent with { SequentialNodeEvidence = evidence };
    }

    private static void RequireEffectReconciliationUpdate(CustomLoopRunStoreResult result, string stage)
    {
        if (result.Status != CustomLoopRunStoreStatus.Updated)
        {
            throw new InvalidOperationException($"The browser Effect Reconciliation fixture {stage} was not persisted: {result.Status}.");
        }
    }

    private static void RequireEffectAttemptUpdate(GovernedLoopEffectAttemptStoreResult result, string stage)
    {
        if (result.Status != GovernedLoopEffectAttemptStoreStatus.Created)
        {
            throw new InvalidOperationException($"The browser Effect Reconciliation fixture {stage} was not persisted: {result.Status}.");
        }
    }
}
