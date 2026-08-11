using EmbodySense.Core.Application.Loops.Admission;
using EmbodySense.Core.Application.Loops.Admission.Models;
using EmbodySense.Core.Application.Loops.Execution.Custom.Models;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Loops.ReceiptRetention;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.Loops.Admission;
using EmbodySense.Core.Common.Loops.Admission.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Common.Loops.Sequential;
using EmbodySense.Core.Common.Loops.Sequential.Models;

namespace EmbodySense.Core.Application.Loops.Sequential;

/// <summary>Coordinates one pre-captured canonical invocation through admission, materialization, receipt completion, and ordered execution.</summary>
/// <remarks>
/// The coordinator reads no mutable graph, role, grant, capability, model, or context state. The caller must durably begin the
/// canonical request-and-artifact-bound invocation receipt before this method is called. The coordinator binds its immutable snapshot before
/// admission, derives the adapter run binding only from the committed canonical receipt, and never dispatches a non-Admitted run.
/// </remarks>
public sealed class GovernedLoopSequentialInvocationCoordinator
{
    private static readonly TimeSpan _integrityWriteTimeout = TimeSpan.FromSeconds(30);
    private const string BoundDetail = "The canonical sequential invocation is durably bound to its exact pre-admission context snapshot.";
    private const string AdmittedDetail = "The canonical admission and exact ordered-run identity are durable before first dispatch.";
    private const string RejectedDetail = "Canonical admission committed a definitive rejection; no run or provider request was created.";
    private readonly string _workspaceId;
    private readonly ICustomLoopInvocationOperationStore _operationStore;
    private readonly CustomLoopInvocationReceiptRetentionService _receiptRetention;
    private readonly IGovernedLoopAdmissionService _admissionService;
    private readonly IGovernedLoopSequentialRunMaterializer _materializer;
    private readonly IGovernedLoopSequentialOrderedRuntime _orderedRuntime;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates the Application-owned canonical sequential invocation coordinator.</summary>
    public GovernedLoopSequentialInvocationCoordinator(
        string workspaceId,
        ICustomLoopInvocationOperationStore operationStore,
        CustomLoopInvocationReceiptRetentionService receiptRetention,
        IGovernedLoopAdmissionService admissionService,
        IGovernedLoopSequentialRunMaterializer materializer,
        IGovernedLoopSequentialOrderedRuntime orderedRuntime,
        TimeProvider? timeProvider = null)
    {
        if (!ContextualRoleWorkspaceId.IsValid(workspaceId))
        {
            throw new ArgumentException("The coordinator requires the server-owned canonical workspace identity.", nameof(workspaceId));
        }

        _workspaceId = workspaceId;
        _operationStore = operationStore ?? throw new ArgumentNullException(nameof(operationStore));
        _receiptRetention = receiptRetention ?? throw new ArgumentNullException(nameof(receiptRetention));
        _admissionService = admissionService ?? throw new ArgumentNullException(nameof(admissionService));
        _materializer = materializer ?? throw new ArgumentNullException(nameof(materializer));
        _orderedRuntime = orderedRuntime ?? throw new ArgumentNullException(nameof(orderedRuntime));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Admits and conditionally dispatches one exact canonical sequential invocation.</summary>
    public async Task<GovernedLoopSequentialInvocationResult> InvokeAsync(
        GovernedLoopSequentialInvocationRequest? request,
        CancellationToken cancellationToken = default)
    {
        if (request is null
            || request.SchemaVersion != GovernedLoopSequentialInvocationRequest.CurrentSchemaVersion
            || !GovernedLoopAdmissionRequestHash.Matches(request.AdmissionRequest)
            || !GovernedLoopSequentialContractValidator.Validate(request.InvocationSnapshot).IsValid
            || !string.Equals(request.AdmissionRequest.InvocationPayloadHash, request.InvocationSnapshot.ContentHash, StringComparison.Ordinal)
            || !Equals(request.AdmissionRequest.Publication?.Revision, request.Artifact?.RevisionArtifact?.Revision))
        {
            return Result(GovernedLoopSequentialInvocationStatus.Invalid, detail: "The canonical sequential invocation request is malformed or its immutable inputs do not compose.");
        }

        var artifact = request.Artifact!;
        var projection = GovernedLoopSequentialLegacyDefinitionProjector.ProjectPrepared(
            request.AdmissionRequest.OperationId,
            request.InvocationSnapshot,
            request.Plan,
            artifact);
        if (projection.Status != GovernedLoopSequentialLegacyDefinitionProjectionStatus.Ready || projection.Definition is null)
        {
            return Result(GovernedLoopSequentialInvocationStatus.Invalid, detail: $"The exact pre-admission ordered-runtime projection was rejected with `{projection.Status}`.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        CustomLoopInvocationOperation? operation;
        try
        {
            operation = await _operationStore.GetAsync(request.AdmissionRequest.OperationId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Result(GovernedLoopSequentialInvocationStatus.Unavailable, detail: $"The pre-admission invocation receipt could not be read safely: {exception.GetType().Name}.");
        }

        if (operation is null)
        {
            return Result(GovernedLoopSequentialInvocationStatus.NotFound, detail: "The required pre-admission invocation receipt does not exist; no admission or provider work occurred.");
        }

        bool matchesOperation;
        try
        {
            matchesOperation = MatchesOperationEnvelope(operation, request, projection.Definition.ContentHash);
        }
        catch (Exception)
        {
            matchesOperation = false;
        }

        if (!matchesOperation)
        {
            return Result(GovernedLoopSequentialInvocationStatus.Conflict, detail: "The invocation operation is bound to a different graph, actor, role, model, prompt, or surface envelope.");
        }

        var binding = await BindInvocationAsync(operation, request, cancellationToken).ConfigureAwait(false);
        if (binding.Status != OperationResolutionStatus.Ready || binding.Operation is null)
        {
            return Result(MapOperationResolution(binding.Status), detail: binding.Detail);
        }

        operation = binding.Operation;
        GovernedLoopAdmissionResult admission;
        try
        {
            admission = await _admissionService.AdmitAsync(request.AdmissionRequest, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Result(GovernedLoopSequentialInvocationStatus.Unavailable, detail: $"Canonical admission could not return a safe result: {exception.GetType().Name}.");
        }

        if (!string.Equals(admission.OperationId, request.AdmissionRequest.OperationId, StringComparison.Ordinal)
            || !string.Equals(admission.RequestHash, request.AdmissionRequest.RequestHash, StringComparison.Ordinal))
        {
            return Result(GovernedLoopSequentialInvocationStatus.Invalid, admission, detail: "Canonical admission returned substituted operation or request coordinates.");
        }

        if (admission.Status is not (GovernedLoopAdmissionStatus.Admitted or GovernedLoopAdmissionStatus.Replayed or GovernedLoopAdmissionStatus.Rejected))
        {
            return Result(MapAdmissionStatus(admission.Status), admission, detail: $"Canonical admission returned `{admission.Status}` without an executable receipt.");
        }

        var outcome = admission.Outcome;
        if (outcome is null
            || !GovernedLoopAdmissionValidator.Validate(outcome).IsValid
            || !MatchesIntent(outcome.Intent, request))
        {
            return Result(GovernedLoopSequentialInvocationStatus.Invalid, admission, detail: "Canonical admission returned malformed or substituted terminal evidence.");
        }

        if (outcome.Disposition == GovernedLoopAdmissionDisposition.Rejected)
        {
            if (admission.Status is not (GovernedLoopAdmissionStatus.Rejected or GovernedLoopAdmissionStatus.Replayed)
                || outcome.Receipt is not null
                || outcome.Rejection is null)
            {
                return Result(GovernedLoopSequentialInvocationStatus.Invalid, admission, detail: "Canonical admission returned an inconsistent rejection disposition.");
            }

            var completedRejection = await CompleteInvocationAsync(
                operation,
                CustomLoopInvocationOutcome.Rejected,
                GovernedLoopAdmissionStatus.Rejected.ToString(),
                runId: null,
                RejectedDetail).ConfigureAwait(false);
            return completedRejection.Status == OperationResolutionStatus.Ready
                ? Result(GovernedLoopSequentialInvocationStatus.Rejected, admission, detail: RejectedDetail)
                : Result(MapOperationResolution(completedRejection.Status), admission, detail: completedRejection.Detail);
        }

        if (outcome.Disposition != GovernedLoopAdmissionDisposition.Admitted
            || admission.Status is not (GovernedLoopAdmissionStatus.Admitted or GovernedLoopAdmissionStatus.Replayed)
            || outcome.Receipt is not { } receipt
            || outcome.Rejection is not null
            || !HasExactCapabilityRoots(receipt, artifact))
        {
            return Result(GovernedLoopSequentialInvocationStatus.Invalid, admission, detail: "Canonical admission did not return the exact graph capability roots and successful receipt required for execution.");
        }

        var adapterBinding = GovernedLoopSequentialContractHash.Apply(new GovernedLoopSequentialAdapterBinding(
            GovernedLoopSequentialAdapterBinding.CurrentSchemaVersion,
            receipt.Intent.WorkspaceId,
            receipt.Evidence.Binding,
            request.AdmissionRequest.OperationId,
            receipt.ContentHash,
            request.AdmissionRequest.RequestHash,
            request.InvocationSnapshot.ContentHash,
            artifact.ArtifactHash,
            artifact.LayoutHash,
            string.Empty));
        var materialization = await _materializer.MaterializeAsync(
            new GovernedLoopSequentialMaterializationRequest(
                GovernedLoopSequentialMaterializationRequest.CurrentSchemaVersion,
                request.AdmissionRequest,
                receipt,
                artifact,
                request.Plan,
                request.InvocationSnapshot,
                adapterBinding),
            cancellationToken).ConfigureAwait(false);
        if (!materialization.IsReady || materialization.Run is not { } run || materialization.Anchor is null)
        {
            return Result(
                MapMaterializationStatus(materialization.Status),
                admission,
                materialization,
                run: materialization.Run,
                detail: materialization.Detail);
        }

        var completedAdmission = await CompleteInvocationAsync(
                operation,
                CustomLoopInvocationOutcome.Admitted,
                GovernedLoopAdmissionStatus.Admitted.ToString(),
                run.Id,
                AdmittedDetail).ConfigureAwait(false);
        if (completedAdmission.Status != OperationResolutionStatus.Ready)
        {
            return Result(
                MapOperationResolution(completedAdmission.Status),
                admission,
                materialization,
                run: run,
                detail: completedAdmission.Detail);
        }

        if (run.IsTerminal)
        {
            return Result(
                GovernedLoopSequentialInvocationStatus.Terminal,
                admission,
                materialization,
                run: run,
                detail: "The exact canonical run is already terminal; no ordered execution was repeated.");
        }

        if (run.Status != CustomLoopRunStatus.Admitted || !CustomLoopRunValidator.ValidateForDispatch(run).IsValid)
        {
            return Result(
                GovernedLoopSequentialInvocationStatus.RecoveryRequired,
                admission,
                materialization,
                run: run,
                detail: $"The exact canonical run is `{run.Status}` and requires recovery or an explicit lifecycle transition; first dispatch was not repeated.");
        }

        try
        {
            var execution = await _orderedRuntime.RunAsync(
                new GovernedLoopSequentialOrderedRunRequest(
                    GovernedLoopSequentialOrderedRunRequest.CurrentSchemaVersion,
                    materialization.Anchor,
                    request.Plan,
                    artifact,
                    request.AdmissionRequest.ActorId.Value),
                cancellationToken).ConfigureAwait(false);
            return Result(
                GovernedLoopSequentialInvocationStatus.Executed,
                admission,
                materialization,
                execution,
                execution.Run ?? run,
                execution.Detail);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Result(
                GovernedLoopSequentialInvocationStatus.RecoveryRequired,
                admission,
                materialization,
                run: run,
                detail: $"Ordered execution returned no safe result ({exception.GetType().Name}); durable recovery must determine whether provider work began.");
        }
    }

    private async Task<OperationResolution> BindInvocationAsync(
        CustomLoopInvocationOperation operation,
        GovernedLoopSequentialInvocationRequest request,
        CancellationToken cancellationToken)
    {
        var snapshot = request.InvocationSnapshot;
        if (operation.BindingState == CustomLoopInvocationBindingState.CapturedContext)
        {
            return MatchesBoundInvocation(operation, snapshot)
                && (operation.State == CustomLoopInvocationOperationState.Complete || IsPendingReceiptShape(operation))
                ? new OperationResolution(OperationResolutionStatus.Ready, operation, "The exact invocation snapshot was already bound durably.")
                : new OperationResolution(OperationResolutionStatus.Conflict, operation, "The invocation receipt is bound to a different context or conversation snapshot.");
        }

        if (operation.BindingState != CustomLoopInvocationBindingState.Unbound
            || !IsPendingReceiptShape(operation)
            || operation.SequentialInvocationSnapshot is not null)
        {
            return new OperationResolution(OperationResolutionStatus.Conflict, operation, "The invocation receipt is not in the exact unbound or captured-context state required for canonical admission.");
        }

        var context = new CustomLoopContextSnapshot(
            CustomLoopContextSnapshot.CurrentSchemaVersion,
            snapshot.ContextCapturedAtUtc,
            snapshot.ContextManifest.ToArray(),
            string.Empty);
        var candidate = operation with
        {
            BindingState = CustomLoopInvocationBindingState.CapturedContext,
            InvokingConversationId = snapshot.InvokingConversation?.ConversationId,
            ContextIdentityHash = CustomLoopContextSnapshotHash.ComputeIdentity(context),
            UpdatedAtUtc = UtcNow(operation.UpdatedAtUtc, snapshot.ContextCapturedAtUtc),
            Detail = BoundDetail,
            SequentialInvocationSnapshot = snapshot,
        };

        cancellationToken.ThrowIfCancellationRequested();
        CustomLoopInvocationOperationStoreResult? stored = null;
        Exception? writeFailure = null;
        try
        {
            stored = await _receiptRetention.BindAsync(candidate, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            writeFailure = exception;
        }

        if (stored?.Status is CustomLoopInvocationOperationStoreStatus.Bound or CustomLoopInvocationOperationStoreStatus.Replayed
            && stored.Operation is { } durable
            && MatchesStoredOperation(durable, candidate)
            && MatchesBoundInvocation(durable, snapshot))
        {
            return new OperationResolution(OperationResolutionStatus.Ready, durable, "The exact invocation snapshot is durable before admission.");
        }

        var reconciled = await ReadOperationForIntegrityAsync(operation.OperationId).ConfigureAwait(false);
        if (reconciled is not null && MatchesStoredOperation(reconciled, candidate) && MatchesBoundInvocation(reconciled, snapshot))
        {
            return new OperationResolution(OperationResolutionStatus.Ready, reconciled, "The exact invocation snapshot committed before an uncertain binding response and was authenticated by replay.");
        }

        var status = stored?.Status is CustomLoopInvocationOperationStoreStatus.Bound or CustomLoopInvocationOperationStoreStatus.Replayed
            ? OperationResolutionStatus.Conflict
            : stored is null
                ? OperationResolutionStatus.Unavailable
                : MapStoreStatus(stored.Status);
        var detail = writeFailure is null
            ? $"The invocation snapshot binding returned `{stored?.Status}` and exact replay could not prove a durable result."
            : $"The invocation snapshot binding returned {writeFailure.GetType().Name}, and exact replay could not prove a durable result.";
        return new OperationResolution(status, reconciled ?? stored?.Operation, detail);
    }

    private async Task<OperationResolution> CompleteInvocationAsync(
        CustomLoopInvocationOperation operation,
        CustomLoopInvocationOutcome outcome,
        string admissionStatus,
        string? runId,
        string detail)
    {
        if (operation.State == CustomLoopInvocationOperationState.Complete)
        {
            return MatchesCompletedInvocation(operation, operation, outcome, admissionStatus, runId)
                ? new OperationResolution(OperationResolutionStatus.Ready, operation, "The exact invocation receipt was already complete.")
                : new OperationResolution(OperationResolutionStatus.Conflict, operation, "The completed invocation receipt is bound to a different canonical outcome or run.");
        }

        var candidate = operation with
        {
            UpdatedAtUtc = UtcNow(operation.UpdatedAtUtc),
            State = CustomLoopInvocationOperationState.Complete,
            Outcome = outcome,
            AdmissionStatus = admissionStatus,
            RunId = runId,
            ValidationErrors = [],
            Detail = detail,
        };
        CustomLoopInvocationOperationStoreResult? stored = null;
        try
        {
            using var integrityWindow = new CancellationTokenSource(_integrityWriteTimeout);
            stored = await _receiptRetention.CompleteAsync(candidate, integrityWindow.Token).ConfigureAwait(false);
            if (stored.Status is CustomLoopInvocationOperationStoreStatus.Completed or CustomLoopInvocationOperationStoreStatus.Replayed
                && stored.Operation is { } durable
                && MatchesCompletedInvocation(durable, candidate, outcome, admissionStatus, runId))
            {
                return new OperationResolution(OperationResolutionStatus.Ready, durable, "The exact canonical invocation receipt is complete.");
            }
        }
        catch
        {
            // Completion may have committed before the adapter failed. Reconcile below using an independent integrity window.
        }

        var reconciled = await ReadOperationForIntegrityAsync(operation.OperationId).ConfigureAwait(false);
        if (reconciled is not null && MatchesCompletedInvocation(reconciled, candidate, outcome, admissionStatus, runId))
        {
            return new OperationResolution(OperationResolutionStatus.Ready, reconciled, "The exact invocation receipt committed before an uncertain response and was authenticated by replay.");
        }

        var unresolvedStatus = stored?.Status is CustomLoopInvocationOperationStoreStatus.Completed or CustomLoopInvocationOperationStoreStatus.Replayed
            ? OperationResolutionStatus.Conflict
            : stored is null
                ? OperationResolutionStatus.Unavailable
                : MapStoreStatus(stored.Status);
        return unresolvedStatus != OperationResolutionStatus.Unavailable
            ? new OperationResolution(unresolvedStatus, reconciled ?? stored?.Operation, $"The exact invocation receipt completion returned `{stored?.Status}` and could not be authenticated; provider dispatch is forbidden.")
            : new OperationResolution(OperationResolutionStatus.Unavailable, reconciled, "The exact invocation receipt completion could not be authenticated; provider dispatch is forbidden.");
    }

    private async Task<CustomLoopInvocationOperation?> ReadOperationForIntegrityAsync(string operationId)
    {
        try
        {
            using var integrityWindow = new CancellationTokenSource(_integrityWriteTimeout);
            return await _operationStore.GetAsync(operationId, integrityWindow.Token).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private static bool MatchesOperationEnvelope(
        CustomLoopInvocationOperation operation,
        GovernedLoopSequentialInvocationRequest request,
        string definitionHash)
    {
        var admission = request.AdmissionRequest;
        var invocation = request.InvocationSnapshot;
        var expectedRequestHash = CustomLoopInvocationRequestHash.ComputeSequential(
            admission.OperationId,
            request.Artifact.Graph.GraphId,
            1,
            definitionHash,
            admission.ActorId.Value,
            admission.Surface,
            request.Artifact.Graph.OwningRole.Identity.RoleId,
            invocation.TriggerPrompt,
            invocation.ModelSnapshot.Provider,
            invocation.ModelSnapshot.Model,
            admission.RequestHash,
            request.Artifact.ArtifactHash);
        return operation.SchemaVersion == CustomLoopInvocationOperation.CurrentSchemaVersion
            && CustomLoopInvocationRequestHash.Matches(operation)
            && string.Equals(operation.RequestHash, expectedRequestHash, StringComparison.Ordinal)
            && string.Equals(operation.OperationId, admission.OperationId, StringComparison.Ordinal)
            && string.Equals(operation.LoopId, request.Artifact.Graph.GraphId, StringComparison.Ordinal)
            && operation.ExpectedDefinitionVersion == 1
            && string.Equals(operation.ExpectedDefinitionHash, definitionHash, StringComparison.Ordinal)
            && string.Equals(operation.Actor, admission.ActorId.Value, StringComparison.Ordinal)
            && string.Equals(operation.Surface, admission.Surface, StringComparison.Ordinal)
            && string.Equals(operation.CurrentRoleId, request.Artifact.Graph.OwningRole.Identity.RoleId, StringComparison.Ordinal)
            && string.Equals(operation.InvocationPromptHash, CustomLoopInvocationRequestHash.ComputePromptHash(invocation.TriggerPrompt), StringComparison.Ordinal)
            && string.Equals(operation.Provider, invocation.ModelSnapshot.Provider, StringComparison.Ordinal)
            && string.Equals(operation.Model, invocation.ModelSnapshot.Model, StringComparison.Ordinal)
            && string.Equals(operation.SequentialAdmissionRequestHash, admission.RequestHash, StringComparison.Ordinal)
            && string.Equals(operation.SequentialArtifactHash, request.Artifact.ArtifactHash, StringComparison.Ordinal)
            && operation.CreatedAtUtc != default
            && operation.UpdatedAtUtc >= operation.CreatedAtUtc
            && operation.State is CustomLoopInvocationOperationState.Pending or CustomLoopInvocationOperationState.Complete;
    }

    private static bool MatchesOperationEnvelope(
        CustomLoopInvocationOperation left,
        CustomLoopInvocationOperation right)
        => left.SchemaVersion == right.SchemaVersion
            && string.Equals(left.OperationId, right.OperationId, StringComparison.Ordinal)
            && string.Equals(left.RequestHash, right.RequestHash, StringComparison.Ordinal)
            && string.Equals(left.LoopId, right.LoopId, StringComparison.Ordinal)
            && left.ExpectedDefinitionVersion == right.ExpectedDefinitionVersion
            && string.Equals(left.ExpectedDefinitionHash, right.ExpectedDefinitionHash, StringComparison.Ordinal)
            && string.Equals(left.Actor, right.Actor, StringComparison.Ordinal)
            && string.Equals(left.Surface, right.Surface, StringComparison.Ordinal)
            && string.Equals(left.CurrentRoleId, right.CurrentRoleId, StringComparison.Ordinal)
            && string.Equals(left.InvocationPromptHash, right.InvocationPromptHash, StringComparison.Ordinal)
            && string.Equals(left.Provider, right.Provider, StringComparison.Ordinal)
            && string.Equals(left.Model, right.Model, StringComparison.Ordinal)
            && string.Equals(left.SequentialAdmissionRequestHash, right.SequentialAdmissionRequestHash, StringComparison.Ordinal)
            && string.Equals(left.SequentialArtifactHash, right.SequentialArtifactHash, StringComparison.Ordinal);

    private static bool MatchesStoredOperation(
        CustomLoopInvocationOperation actual,
        CustomLoopInvocationOperation expected)
        => MatchesOperationEnvelope(actual, expected)
            && actual.BindingState == expected.BindingState
            && string.Equals(actual.InvokingConversationId, expected.InvokingConversationId, StringComparison.Ordinal)
            && string.Equals(actual.ContextIdentityHash, expected.ContextIdentityHash, StringComparison.Ordinal)
            && actual.CreatedAtUtc == expected.CreatedAtUtc
            && actual.UpdatedAtUtc == expected.UpdatedAtUtc
            && actual.State == expected.State
            && actual.Outcome == expected.Outcome
            && string.Equals(actual.AdmissionStatus, expected.AdmissionStatus, StringComparison.Ordinal)
            && string.Equals(actual.RunId, expected.RunId, StringComparison.Ordinal)
            && actual.ValidationErrors is not null
            && expected.ValidationErrors is not null
            && actual.ValidationErrors.SequenceEqual(expected.ValidationErrors)
            && string.Equals(actual.Detail, expected.Detail, StringComparison.Ordinal)
            && string.Equals(actual.SequentialInvocationSnapshot?.ContentHash, expected.SequentialInvocationSnapshot?.ContentHash, StringComparison.Ordinal);

    private static bool MatchesBoundInvocation(
        CustomLoopInvocationOperation operation,
        GovernedLoopSequentialInvocationSnapshot snapshot)
    {
        var context = new CustomLoopContextSnapshot(
            CustomLoopContextSnapshot.CurrentSchemaVersion,
            snapshot.ContextCapturedAtUtc,
            snapshot.ContextManifest.ToArray(),
            string.Empty);
        return operation.BindingState == CustomLoopInvocationBindingState.CapturedContext
            && operation.SequentialInvocationSnapshot is { } durable
            && GovernedLoopSequentialContractValidator.Validate(durable).IsValid
            && string.Equals(durable.ContentHash, snapshot.ContentHash, StringComparison.Ordinal)
            && string.Equals(operation.InvokingConversationId, snapshot.InvokingConversation?.ConversationId, StringComparison.Ordinal)
            && string.Equals(operation.ContextIdentityHash, CustomLoopContextSnapshotHash.ComputeIdentity(context), StringComparison.Ordinal);
    }

    private static bool IsPendingReceiptShape(CustomLoopInvocationOperation operation)
        => operation.State == CustomLoopInvocationOperationState.Pending
            && operation.Outcome == CustomLoopInvocationOutcome.Unknown
            && string.IsNullOrEmpty(operation.AdmissionStatus)
            && operation.RunId is null
            && operation.ValidationErrors is { Length: 0 };

    private static bool MatchesCompletedInvocation(
        CustomLoopInvocationOperation operation,
        CustomLoopInvocationOperation expected,
        CustomLoopInvocationOutcome outcome,
        string admissionStatus,
        string? runId)
        => MatchesStoredOperation(operation, expected)
            && operation.State == CustomLoopInvocationOperationState.Complete
            && operation.Outcome == outcome
            && string.Equals(operation.AdmissionStatus, admissionStatus, StringComparison.Ordinal)
            && string.Equals(operation.RunId, runId, StringComparison.Ordinal)
            && operation.ValidationErrors is { Length: 0 };

    private bool MatchesIntent(
        GovernedLoopAdmissionIntent intent,
        GovernedLoopSequentialInvocationRequest request)
    {
        var admission = request.AdmissionRequest;
        return string.Equals(intent.WorkspaceId, _workspaceId, StringComparison.Ordinal)
            && string.Equals(intent.OperationId, admission.OperationId, StringComparison.Ordinal)
            && string.Equals(intent.RequestHash, admission.RequestHash, StringComparison.Ordinal)
            && Equals(intent.Publication, admission.Publication)
            && Equals(intent.AuthorityGrant, admission.AuthorityGrant)
            && Equals(intent.Role, request.Artifact.Graph.OwningRole)
            && Equals(intent.ActorId, admission.ActorId)
            && string.Equals(intent.Surface, admission.Surface, StringComparison.Ordinal)
            && string.Equals(intent.GraphArtifactHash, request.Artifact.ArtifactHash, StringComparison.Ordinal)
            && string.Equals(intent.GraphLayoutHash, request.Artifact.LayoutHash, StringComparison.Ordinal);
    }

    private static bool HasExactCapabilityRoots(
        GovernedLoopAdmissionReceipt receipt,
        GovernedLoopGraphRevisionArtifact artifact)
    {
        var snapshot = receipt.Evidence.CapabilityAdmission;
        var roots = snapshot.Evidence
            .Where(item => item.SubjectId.Equals(snapshot.Requirements.SubjectId)
                && string.Equals(item.Outcome, "Selected", StringComparison.Ordinal))
            .Select(item => item.SelectedIdentity?.Id.Value ?? string.Empty)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        return roots.SequenceEqual(artifact.Graph.AuthorityCeiling.CapabilityIds, StringComparer.Ordinal);
    }

    private DateTimeOffset UtcNow(params DateTimeOffset[] minimums)
    {
        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        foreach (var minimum in minimums)
        {
            if (now < minimum)
            {
                now = minimum;
            }
        }

        return now;
    }

    private static GovernedLoopSequentialInvocationStatus MapAdmissionStatus(GovernedLoopAdmissionStatus status)
        => status switch
        {
            GovernedLoopAdmissionStatus.Conflict => GovernedLoopSequentialInvocationStatus.Conflict,
            GovernedLoopAdmissionStatus.Unavailable or GovernedLoopAdmissionStatus.Ambiguous => GovernedLoopSequentialInvocationStatus.Unavailable,
            GovernedLoopAdmissionStatus.LimitExceeded => GovernedLoopSequentialInvocationStatus.LimitExceeded,
            _ => GovernedLoopSequentialInvocationStatus.Invalid,
        };

    private static GovernedLoopSequentialInvocationStatus MapMaterializationStatus(GovernedLoopSequentialMaterializationStatus status)
        => status switch
        {
            GovernedLoopSequentialMaterializationStatus.Conflict => GovernedLoopSequentialInvocationStatus.Conflict,
            GovernedLoopSequentialMaterializationStatus.NonterminalRunExists => GovernedLoopSequentialInvocationStatus.RecoveryRequired,
            GovernedLoopSequentialMaterializationStatus.Unavailable => GovernedLoopSequentialInvocationStatus.Unavailable,
            GovernedLoopSequentialMaterializationStatus.LimitExceeded => GovernedLoopSequentialInvocationStatus.LimitExceeded,
            GovernedLoopSequentialMaterializationStatus.AuditUnavailable => GovernedLoopSequentialInvocationStatus.AuditUnavailable,
            GovernedLoopSequentialMaterializationStatus.AuditConflict => GovernedLoopSequentialInvocationStatus.Conflict,
            _ => GovernedLoopSequentialInvocationStatus.Invalid,
        };

    private static GovernedLoopSequentialInvocationStatus MapOperationResolution(OperationResolutionStatus status)
        => status switch
        {
            OperationResolutionStatus.Conflict => GovernedLoopSequentialInvocationStatus.Conflict,
            OperationResolutionStatus.NotFound => GovernedLoopSequentialInvocationStatus.NotFound,
            OperationResolutionStatus.LimitExceeded => GovernedLoopSequentialInvocationStatus.LimitExceeded,
            OperationResolutionStatus.AuditUnavailable => GovernedLoopSequentialInvocationStatus.AuditUnavailable,
            OperationResolutionStatus.Invalid => GovernedLoopSequentialInvocationStatus.Invalid,
            _ => GovernedLoopSequentialInvocationStatus.Unavailable,
        };

    private static OperationResolutionStatus MapStoreStatus(CustomLoopInvocationOperationStoreStatus status)
        => status switch
        {
            CustomLoopInvocationOperationStoreStatus.Conflict => OperationResolutionStatus.Conflict,
            CustomLoopInvocationOperationStoreStatus.NotFound => OperationResolutionStatus.NotFound,
            CustomLoopInvocationOperationStoreStatus.LimitExceeded or
            CustomLoopInvocationOperationStoreStatus.RetentionRequired => OperationResolutionStatus.LimitExceeded,
            CustomLoopInvocationOperationStoreStatus.RetentionAuditUnavailable => OperationResolutionStatus.AuditUnavailable,
            CustomLoopInvocationOperationStoreStatus.RetentionInvalid => OperationResolutionStatus.Invalid,
            _ => OperationResolutionStatus.Unavailable,
        };

    private static GovernedLoopSequentialInvocationResult Result(
        GovernedLoopSequentialInvocationStatus status,
        GovernedLoopAdmissionResult? admission = null,
        GovernedLoopSequentialMaterializationResult? materialization = null,
        CustomLoopOrderedRunResult? execution = null,
        CustomLoopRunRecord? run = null,
        string detail = "")
        => new(status, admission, materialization, execution, run, detail);

    private enum OperationResolutionStatus
    {
        Ready,
        Conflict,
        NotFound,
        LimitExceeded,
        AuditUnavailable,
        Invalid,
        Unavailable,
    }

    private sealed record OperationResolution(
        OperationResolutionStatus Status,
        CustomLoopInvocationOperation? Operation,
        string Detail);
}
