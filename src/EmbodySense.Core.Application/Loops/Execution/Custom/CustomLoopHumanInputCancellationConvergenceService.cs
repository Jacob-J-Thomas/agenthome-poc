using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Governance.Authority.Grants;
using EmbodySense.Core.Application.HumanInput.Lifecycle;
using EmbodySense.Core.Application.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Application.Loops.Execution.Custom.Models;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.HumanInput;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.HumanInput.Checkpoints;
using EmbodySense.Core.Common.Loops.HumanInput.Checkpoints.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Loops.Execution.Custom;

/// <summary>Converges one cancel-requested run with the existing canonical Human Input request lifecycle ledger.</summary>
/// <remarks>The service is transient orchestration only. It never owns a second ledger, queue, or recovery record: the
/// parent control receipt, canonical run, and request lifecycle evidence are reread under the shared reentrant workspace
/// authority transaction. Each successor retires at most one checkpoint so the run validator remains the authority on
/// append-only checkpoint history.</remarks>
public sealed class CustomLoopHumanInputCancellationConvergenceService : ICustomLoopHumanInputCancellationConvergence
{
    private const int MaximumCheckpointReconciliationAttempts = 32;
    private const string CancellationReasonText = "loop-cancellation-human-input";
    private readonly ICapabilityAuthorityTransaction _authorityTransaction;
    private readonly ICustomLoopControlOperationStore _controlOperations;
    private readonly IAuthorityGrantResolver _grantResolver;
    private readonly IHumanInputRequestLifecycleStore _requests;
    private readonly ICustomLoopRunStore _runs;
    private readonly TimeProvider _timeProvider;
    private readonly string _workspaceId;

    /// <summary>Creates one cancellation convergence boundary over the canonical run, control, request, grant, and authority ports.</summary>
    /// <param name="runs">The sole canonical custom-loop run store.</param>
    /// <param name="controlOperations">The durable parent lifecycle-control receipt store.</param>
    /// <param name="requests">The sole canonical Human Input request lifecycle store.</param>
    /// <param name="grantResolver">The existing lifecycle grant resolver. Cancel does not resolve a grant but the lifecycle service requires the shared port.</param>
    /// <param name="authorityTransaction">The shared reentrant workspace authority fence used by publication and request lifecycle persistence.</param>
    /// <param name="workspaceId">The server-configured canonical workspace scope.</param>
    /// <param name="timeProvider">The trusted lifecycle clock, or the system clock when omitted.</param>
    /// <exception cref="ArgumentNullException">Thrown when a required canonical dependency is null.</exception>
    /// <exception cref="ArgumentException">Thrown when the workspace scope is not canonical.</exception>
    public CustomLoopHumanInputCancellationConvergenceService(
        ICustomLoopRunStore runs,
        ICustomLoopControlOperationStore controlOperations,
        IHumanInputRequestLifecycleStore requests,
        IAuthorityGrantResolver grantResolver,
        ICapabilityAuthorityTransaction authorityTransaction,
        string workspaceId,
        TimeProvider? timeProvider = null)
    {
        _runs = runs ?? throw new ArgumentNullException(nameof(runs));
        _controlOperations = controlOperations ?? throw new ArgumentNullException(nameof(controlOperations));
        _requests = requests ?? throw new ArgumentNullException(nameof(requests));
        _grantResolver = grantResolver ?? throw new ArgumentNullException(nameof(grantResolver));
        _authorityTransaction = authorityTransaction ?? throw new ArgumentNullException(nameof(authorityTransaction));
        if (!ContextualRoleWorkspaceId.IsValid(workspaceId)) throw new ArgumentException("Workspace id must use the canonical workspace-sha256 scope contract.", nameof(workspaceId));
        _workspaceId = workspaceId;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<CustomLoopHumanInputCancellationConvergenceResult> ConvergeAsync(
        CustomLoopRunRecord run,
        string cancellationOperationId,
        CancellationToken cancellationToken = default)
    {
        if (run is null
            || !CustomLoopArtifactIdentifier.IsValid(run.Id)
            || !CustomLoopArtifactIdentifier.IsValid(cancellationOperationId, CustomLoopLimits.MaxMutationOperationIdCharacters))
        {
            return Result(CustomLoopHumanInputCancellationConvergenceStatus.Corrupt, run, "Cancellation convergence requires one canonical run and parent control-operation identity.");
        }

        try
        {
            return await _authorityTransaction.ExecuteAsync(
                token => ConvergeUnderFenceAsync(run.Id, cancellationOperationId, token),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Result(CustomLoopHumanInputCancellationConvergenceStatus.Unavailable, run, "The shared cancellation convergence authority boundary was unavailable; the run remains CancelRequested for exact replay.");
        }
    }

    private async Task<CustomLoopHumanInputCancellationConvergenceResult> ConvergeUnderFenceAsync(
        string runId,
        string cancellationOperationId,
        CancellationToken cancellationToken)
    {
        var control = await ReadControlAsync(cancellationOperationId, cancellationToken).ConfigureAwait(false);
        if (!IsCancellationControl(control, runId))
        {
            return Result(CustomLoopHumanInputCancellationConvergenceStatus.Corrupt, null, "The retained parent control receipt is missing, divergent, or not a retryable Cancel request for this run.");
        }

        for (var attempt = 0; attempt < MaximumCheckpointReconciliationAttempts; attempt++)
        {
            var run = await ReadRunAsync(runId, cancellationToken).ConfigureAwait(false);
            if (run is null)
            {
                return Result(CustomLoopHumanInputCancellationConvergenceStatus.Unavailable, null, "The canonical run could not be reread while cancellation convergence held the workspace fence.");
            }

            if (!CustomLoopRunValidator.Validate(run).IsValid
                || run.Status != CustomLoopRunStatus.CancelRequested
                || !HasParentCancellationTransition(run, control!))
            {
                return Result(CustomLoopHumanInputCancellationConvergenceStatus.Corrupt, run, "The retained cancellation control receipt no longer matches one valid CancelRequested run transition.");
            }

            if (run.HumanInputWaitingCheckpoints.Count == 0)
            {
                return Result(CustomLoopHumanInputCancellationConvergenceStatus.NotApplicable, run, "The CancelRequested run retains no canonical Human Input checkpoint.");
            }

            var next = run.HumanInputWaitingCheckpoints
                .OrderBy(checkpoint => checkpoint.Binding.ActivationOrdinal)
                .ThenBy(checkpoint => checkpoint.Binding.NodeVisitOrdinal)
                .ThenBy(checkpoint => checkpoint.Binding.CheckpointId, StringComparer.Ordinal)
                .FirstOrDefault(checkpoint => checkpoint.Posture == GovernedLoopHumanInputWaitingCheckpointPosture.Pending);
            if (next is null)
            {
                return HasBlockingCheckpoint(run)
                    ? Result(CustomLoopHumanInputCancellationConvergenceStatus.Blocked, run, "A Human Input answer or review-required checkpoint won before loop cancellation could safely retire it; the run remains CancelRequested for explicit reconciliation.")
                    : Result(CustomLoopHumanInputCancellationConvergenceStatus.Converged, run, "Every canonical Human Input checkpoint has retained terminal proof; the caller may now terminalize the run as Cancelled.");
            }

            var reconciliation = await ReconcilePendingCheckpointAsync(run, next, control!, cancellationToken).ConfigureAwait(false);
            if (reconciliation.Status != CustomLoopHumanInputCheckpointReconciliationStatus.Advanced)
            {
                return Result(reconciliation.Status switch
                {
                    CustomLoopHumanInputCheckpointReconciliationStatus.Pending => CustomLoopHumanInputCancellationConvergenceStatus.Pending,
                    CustomLoopHumanInputCheckpointReconciliationStatus.Blocked => CustomLoopHumanInputCancellationConvergenceStatus.Blocked,
                    CustomLoopHumanInputCheckpointReconciliationStatus.Conflict => CustomLoopHumanInputCancellationConvergenceStatus.Conflict,
                    CustomLoopHumanInputCheckpointReconciliationStatus.Unavailable => CustomLoopHumanInputCancellationConvergenceStatus.Unavailable,
                    _ => CustomLoopHumanInputCancellationConvergenceStatus.Corrupt,
                }, reconciliation.Run ?? run, reconciliation.Detail);
            }
        }

        return Result(CustomLoopHumanInputCancellationConvergenceStatus.Pending, null, "The bounded cancellation convergence pass retired checkpoint evidence but reached its retry budget; the retained CancelRequested run can replay the same parent operation.");
    }

    private async Task<CustomLoopHumanInputCheckpointReconciliation> ReconcilePendingCheckpointAsync(
        CustomLoopRunRecord run,
        GovernedLoopHumanInputWaitingCheckpoint checkpoint,
        CustomLoopControlOperation control,
        CancellationToken cancellationToken)
    {
        HumanInputRequestLifecycleStoreReadResult? read;
        try
        {
            read = await _requests.ReadAsync(checkpoint.Request.RequestId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Reconciliation(CustomLoopHumanInputCheckpointReconciliationStatus.Unavailable, run, "The canonical Human Input request lifecycle could not be read; no checkpoint or run terminal state changed.");
        }

        if (read is null || !Enum.IsDefined(read.Status) || read.StoreGeneration < 0)
        {
            return Reconciliation(CustomLoopHumanInputCheckpointReconciliationStatus.Corrupt, run, "The canonical Human Input request lifecycle returned malformed read evidence.");
        }

        if (read.Status == HumanInputRequestLifecycleStoreReadStatus.NotFound
            && read.PrimarySnapshot is null
            && read.RelatedSnapshot is null
            && read.ExistingOperation is null)
        {
            return await RetireCheckpointAsync(run, checkpoint, null, cancellationToken).ConfigureAwait(false);
        }

        if (read.Status != HumanInputRequestLifecycleStoreReadStatus.Ready
            || read.PrimarySnapshot is null
            || read.RelatedSnapshot is not null
            || read.ExistingOperation is not null
            || !HumanInputRequestLifecycleStoreSnapshotGuard.TryCapture(read.PrimarySnapshot, checkpoint.Request.RequestId, out var snapshot)
            || snapshot is null
            || !SnapshotMatchesCheckpoint(snapshot, checkpoint))
        {
            return Reconciliation(CustomLoopHumanInputCheckpointReconciliationStatus.Blocked, run, "The canonical Human Input request lifecycle is unavailable, ambiguous, or no longer bound to this immutable checkpoint; cancellation did not overwrite the observed winner.");
        }

        return snapshot.Head.Status switch
        {
            HumanInputRequestLifecycleStatus.Pending => await CancelPendingRequestAsync(run, checkpoint, control, snapshot, cancellationToken).ConfigureAwait(false),
            HumanInputRequestLifecycleStatus.Cancelled when TryFindCommittedTerminal(snapshot, HumanInputRequestLifecycleOperationKind.Cancel, out var evidence)
                => await RetireCheckpointAsync(run, checkpoint, evidence, cancellationToken).ConfigureAwait(false),
            HumanInputRequestLifecycleStatus.Rejected
                or HumanInputRequestLifecycleStatus.Expired
                or HumanInputRequestLifecycleStatus.Superseded
                or HumanInputRequestLifecycleStatus.Answered
                => Reconciliation(CustomLoopHumanInputCheckpointReconciliationStatus.Blocked, run, "An independent Human Input terminal lifecycle outcome won before loop cancellation; no request evidence was overwritten and the run remains CancelRequested for explicit reconciliation."),
            _ => Reconciliation(CustomLoopHumanInputCheckpointReconciliationStatus.Corrupt, run, "The canonical Human Input request lifecycle returned an unsupported terminal posture.")
        };
    }

    private async Task<CustomLoopHumanInputCheckpointReconciliation> CancelPendingRequestAsync(
        CustomLoopRunRecord run,
        GovernedLoopHumanInputWaitingCheckpoint checkpoint,
        CustomLoopControlOperation control,
        HumanInputRequestLifecycleStoreSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (!AuthorityActorId.TryParse(run.AdmissionActor, out var actor, out _)
            || actor is null
            || !AuthorityPurpose.TryParse(CancellationReasonText, out var reason, out _))
        {
            return Reconciliation(CustomLoopHumanInputCheckpointReconciliationStatus.Corrupt, run, "The admitted actor or internal loop-cancellation lifecycle purpose is invalid.");
        }

        HumanInputRequestLifecycleCommand command;
        try
        {
            command = HumanInputRequestLifecycleCommandHash.Apply(new HumanInputRequestLifecycleCommand(
                HumanInputRequestLifecycleCommand.CurrentSchemaVersion,
                ChildOperationId(control, run, checkpoint),
                HumanInputRequestLifecycleOperationKind.Cancel,
                checkpoint.Request.RequestId,
                snapshot.Head.LifecycleVersion,
                HumanInputRequestLifecycleStatus.Pending,
                snapshot.Head.CurrentRequest,
                checkpoint.Request.Binding,
                null,
                null,
                reason!,
                string.Empty));
        }
        catch (ArgumentException)
        {
            return Reconciliation(CustomLoopHumanInputCheckpointReconciliationStatus.Corrupt, run, "The deterministic child Human Input Cancel command could not be constructed from immutable control and checkpoint evidence.");
        }

        var lifecycle = new HumanInputRequestLifecycleService(
            _requests,
            new LoopCancellationHumanInputRequestLifecycleActorAuthorizer(command, actor, control.RequestHash, _workspaceId),
            _grantResolver,
            _authorityTransaction,
            _workspaceId,
            _timeProvider);
        HumanInputRequestLifecycleMutationResult mutation;
        try
        {
            mutation = await lifecycle.MutateAsync(command, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Reconciliation(CustomLoopHumanInputCheckpointReconciliationStatus.Unavailable, run, "The deterministic child Human Input Cancel operation could not establish a durable outcome.");
        }

        if (mutation is null
            || mutation.Status is not (HumanInputRequestLifecycleMutationStatus.Committed or HumanInputRequestLifecycleMutationStatus.Replayed)
            || mutation.Proof is not
            {
                OperationId: var operationId,
                RequestHash: var requestHash,
                Kind: HumanInputRequestLifecycleOperationKind.Cancel,
                Outcome: HumanInputRequestLifecycleOperationOutcome.Committed,
            }
            || !string.Equals(operationId, command.OperationId, StringComparison.Ordinal)
            || !string.Equals(requestHash, command.RequestHash, StringComparison.Ordinal))
        {
            return mutation?.Status switch
            {
                HumanInputRequestLifecycleMutationStatus.Conflict => Reconciliation(CustomLoopHumanInputCheckpointReconciliationStatus.Conflict, run, "The pending request changed concurrently before the deterministic child Cancel could commit; the parent cancellation receipt remains replayable."),
                HumanInputRequestLifecycleMutationStatus.Unavailable or HumanInputRequestLifecycleMutationStatus.Ambiguous => Reconciliation(CustomLoopHumanInputCheckpointReconciliationStatus.Unavailable, run, "The deterministic child Human Input Cancel outcome is unavailable or ambiguous; the run remains CancelRequested."),
                _ => Reconciliation(CustomLoopHumanInputCheckpointReconciliationStatus.Blocked, run, "The pending request did not produce the exact deterministic child Cancel proof; cancellation preserved the observed request evidence.")
            };
        }

        var proved = await ReadExactCancellationEvidenceAsync(checkpoint, command, cancellationToken).ConfigureAwait(false);
        return proved.Evidence is null
            ? Reconciliation(CustomLoopHumanInputCheckpointReconciliationStatus.Unavailable, run, proved.Detail)
            : await RetireCheckpointAsync(run, checkpoint, proved.Evidence, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(HumanInputRequestLifecycleOperationEvidence? Evidence, string Detail)> ReadExactCancellationEvidenceAsync(
        GovernedLoopHumanInputWaitingCheckpoint checkpoint,
        HumanInputRequestLifecycleCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var read = await _requests.ReadAsync(checkpoint.Request.RequestId, cancellationToken).ConfigureAwait(false);
            if (read is not { Status: HumanInputRequestLifecycleStoreReadStatus.Ready, PrimarySnapshot: not null, RelatedSnapshot: null, ExistingOperation: null }
                || !HumanInputRequestLifecycleStoreSnapshotGuard.TryCapture(read.PrimarySnapshot, checkpoint.Request.RequestId, out var snapshot)
                || snapshot is null
                || !SnapshotMatchesCheckpoint(snapshot, checkpoint)
                || snapshot.Head.Status != HumanInputRequestLifecycleStatus.Cancelled)
            {
                return (null, "The durable request lifecycle could not prove the child Cancel terminal head after its commit boundary.");
            }

            var evidence = snapshot.Operations.SingleOrDefault(item => string.Equals(item.OperationId, command.OperationId, StringComparison.Ordinal));
            return evidence is
            {
                RequestHash: var hash,
                Kind: HumanInputRequestLifecycleOperationKind.Cancel,
                Outcome: HumanInputRequestLifecycleOperationOutcome.Committed,
                ResultHead.Status: HumanInputRequestLifecycleStatus.Cancelled,
            }
                && string.Equals(hash, command.RequestHash, StringComparison.Ordinal)
                ? (evidence, string.Empty)
                : (null, "The durable request lifecycle did not retain one exact child Cancel evidence record.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return (null, "The durable request lifecycle could not be reread after the child Cancel commit boundary.");
        }
    }

    private async Task<CustomLoopHumanInputCheckpointReconciliation> RetireCheckpointAsync(
        CustomLoopRunRecord run,
        GovernedLoopHumanInputWaitingCheckpoint checkpoint,
        HumanInputRequestLifecycleOperationEvidence? cancellationEvidence,
        CancellationToken cancellationToken)
    {
        if (!TryRetireCheckpoint(run, checkpoint, cancellationEvidence?.RecordedAtUtc, out var retired, out var timestamp))
        {
            return Reconciliation(CustomLoopHumanInputCheckpointReconciliationStatus.Blocked, run, "Cancellation could not retain a timestamp-valid checkpoint terminal boundary without contradicting the immutable request response window.");
        }

        CustomLoopRunRecord candidate;
        try
        {
            candidate = run with
            {
                LifecycleVersion = checked(run.LifecycleVersion + 1),
                UpdatedAtUtc = timestamp,
                HumanInputWaitingCheckpoints = ReplaceCheckpoint(run.HumanInputWaitingCheckpoints, retired!),
            };
        }
        catch (OverflowException)
        {
            return Reconciliation(CustomLoopHumanInputCheckpointReconciliationStatus.Corrupt, run, "The run lifecycle version cannot advance to retain the Human Input cancellation boundary.");
        }

        if (!CustomLoopRunValidator.ValidateUpdate(run, candidate).IsValid)
        {
            return Reconciliation(CustomLoopHumanInputCheckpointReconciliationStatus.Corrupt, run, "The proposed one-checkpoint cancellation successor is not a valid canonical run update.");
        }

        try
        {
            var stored = await _runs.UpdateAsync(candidate, run.LifecycleVersion, CancellationToken.None).ConfigureAwait(false);
            return stored.Status switch
            {
                CustomLoopRunStoreStatus.Updated when stored.Run is not null => Reconciliation(CustomLoopHumanInputCheckpointReconciliationStatus.Advanced, stored.Run, "One canonical Human Input checkpoint was durably retired under the parent cancellation receipt."),
                CustomLoopRunStoreStatus.Conflict or CustomLoopRunStoreStatus.TerminalImmutable => Reconciliation(CustomLoopHumanInputCheckpointReconciliationStatus.Conflict, stored.Run ?? run, "The run changed before its one-checkpoint cancellation successor could commit; no second request operation was issued."),
                CustomLoopRunStoreStatus.NotFound => Reconciliation(CustomLoopHumanInputCheckpointReconciliationStatus.Unavailable, null, "The canonical run disappeared while retaining the cancellation checkpoint boundary."),
                _ => Reconciliation(CustomLoopHumanInputCheckpointReconciliationStatus.Unavailable, stored.Run ?? run, "The canonical run store did not acknowledge the cancellation checkpoint successor safely.")
            };
        }
        catch
        {
            return Reconciliation(CustomLoopHumanInputCheckpointReconciliationStatus.Unavailable, run, "The run-store acknowledgement after request terminalization is unavailable; replay will reread exact child Cancel evidence before another checkpoint update.");
        }
    }

    private bool TryRetireCheckpoint(
        CustomLoopRunRecord run,
        GovernedLoopHumanInputWaitingCheckpoint checkpoint,
        DateTimeOffset? evidenceTimestamp,
        out GovernedLoopHumanInputWaitingCheckpoint? retired,
        out DateTimeOffset timestamp)
    {
        retired = null;
        timestamp = default;
        try
        {
            timestamp = evidenceTimestamp?.ToUniversalTime() ?? _timeProvider.GetUtcNow().ToUniversalTime();
            if (timestamp < checkpoint.Evidence[^1].OccurredAtUtc)
            {
                timestamp = checkpoint.Evidence[^1].OccurredAtUtc;
            }
            if (timestamp < run.UpdatedAtUtc)
            {
                timestamp = run.UpdatedAtUtc;
            }
            if (timestamp > checkpoint.Request.Timing.ExpiresAtUtc)
            {
                return false;
            }

            var evidence = GovernedLoopHumanInputWaitingCheckpointContractHash.Apply(new GovernedLoopHumanInputWaitingCheckpointEvidence(
                GovernedLoopHumanInputWaitingCheckpoint.CurrentSchemaVersion,
                checkpoint.Evidence.Length + 1,
                GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Cancelled,
                timestamp,
                null,
                null,
                null,
                null,
                null,
                checkpoint.Evidence[^1].EvidenceHash,
                string.Empty));
            retired = GovernedLoopHumanInputWaitingCheckpointContractHash.Apply(new GovernedLoopHumanInputWaitingCheckpoint(
                checkpoint.SchemaVersion,
                checkpoint.Binding,
                checkpoint.NodeConfiguration,
                checkpoint.ResolvedPolicy,
                checkpoint.Request,
                GovernedLoopHumanInputWaitingCheckpointPosture.Cancelled,
                [.. checkpoint.Evidence, evidence],
                string.Empty));
            return GovernedLoopHumanInputWaitingCheckpointStateTransitionValidator.ValidateTransition(checkpoint, retired).IsValid;
        }
        catch
        {
            retired = null;
            timestamp = default;
            return false;
        }
    }

    private static bool SnapshotMatchesCheckpoint(HumanInputRequestLifecycleStoreSnapshot snapshot, GovernedLoopHumanInputWaitingCheckpoint checkpoint)
    {
        var expected = new HumanInputRequestReference(
            HumanInputRequestReference.CurrentSchemaVersion,
            checkpoint.Request.RequestId,
            checkpoint.Request.RequestVersionId,
            checkpoint.Request.RequestHash);
        var versions = snapshot.RequestVersions.Where(request => string.Equals(request.RequestId, expected.RequestId, StringComparison.Ordinal)
                && string.Equals(request.RequestVersionId, expected.RequestVersionId, StringComparison.Ordinal)
                && string.Equals(request.RequestHash, expected.RequestHash, StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        return versions.Length == 1
            && Equals(versions[0].Binding, checkpoint.Request.Binding)
            && Equals(snapshot.Head.CurrentRequest, expected);
    }

    private static bool TryFindCommittedTerminal(
        HumanInputRequestLifecycleStoreSnapshot snapshot,
        HumanInputRequestLifecycleOperationKind kind,
        out HumanInputRequestLifecycleOperationEvidence? evidence)
    {
        evidence = snapshot.Operations.SingleOrDefault(item => string.Equals(item.OperationId, snapshot.Head.LastOperationId, StringComparison.Ordinal));
        return evidence is
        {
            Kind: var evidenceKind,
            Outcome: HumanInputRequestLifecycleOperationOutcome.Committed,
            ResultHead: not null,
        }
            && evidenceKind == kind
            && Equals(evidence.ResultHead, snapshot.Head);
    }

    private static bool HasBlockingCheckpoint(CustomLoopRunRecord run)
        => run.HumanInputWaitingCheckpoints.Any(checkpoint => checkpoint.Posture is GovernedLoopHumanInputWaitingCheckpointPosture.Pending
            or GovernedLoopHumanInputWaitingCheckpointPosture.AnsweredNotResumed
            or GovernedLoopHumanInputWaitingCheckpointPosture.NeedsReview);

    private static bool HasParentCancellationTransition(CustomLoopRunRecord run, CustomLoopControlOperation operation)
        => run.Events.Count(item => item.Kind == CustomLoopRunEventKind.LifecycleChanged
            && string.Equals(item.EventId, operation.OperationId, StringComparison.Ordinal)
            && item.ControlExpectedLifecycleVersion == operation.ExpectedLifecycleVersion) == 1;

    private static bool IsCancellationControl(CustomLoopControlOperation? operation, string runId)
        => operation is
        {
            SchemaVersion: CustomLoopControlOperation.CurrentSchemaVersion,
            Kind: CustomLoopControlKind.Cancel,
            RunId: var operationRunId,
            State: var state,
            RequestHash: { Length: CustomLoopLimits.Sha256HexCharacters },
        }
            && string.Equals(operationRunId, runId, StringComparison.Ordinal)
            && state is CustomLoopControlOperationState.Pending or CustomLoopControlOperationState.Complete
            && (operation.State != CustomLoopControlOperationState.Complete
                || operation.Outcome is CustomLoopControlStatus.CancelRequested or CustomLoopControlStatus.AuditWarning);

    private async Task<CustomLoopControlOperation?> ReadControlAsync(string operationId, CancellationToken cancellationToken)
    {
        try
        {
            return await _controlOperations.GetAsync(operationId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private async Task<CustomLoopRunRecord?> ReadRunAsync(string runId, CancellationToken cancellationToken)
    {
        try
        {
            return await _runs.GetAsync(runId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static string ChildOperationId(
        CustomLoopControlOperation control,
        CustomLoopRunRecord run,
        GovernedLoopHumanInputWaitingCheckpoint checkpoint)
    {
        var material = string.Join('\n',
            "embodysense-loop-cancellation-human-input-v1",
            control.OperationId,
            control.RequestHash,
            run.Id,
            checkpoint.Binding.CheckpointId,
            checkpoint.CheckpointHash,
            checkpoint.Request.RequestId,
            checkpoint.Request.RequestVersionId,
            checkpoint.Request.RequestHash);
        return "human-input-loop-cancel-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    private static IReadOnlyList<GovernedLoopHumanInputWaitingCheckpoint> ReplaceCheckpoint(
        IReadOnlyList<GovernedLoopHumanInputWaitingCheckpoint> checkpoints,
        GovernedLoopHumanInputWaitingCheckpoint replacement)
        => Array.AsReadOnly(checkpoints.Select(checkpoint => string.Equals(checkpoint.Binding.CheckpointId, replacement.Binding.CheckpointId, StringComparison.Ordinal) ? replacement : checkpoint).ToArray());

    private static CustomLoopHumanInputCancellationConvergenceResult Result(
        CustomLoopHumanInputCancellationConvergenceStatus status,
        CustomLoopRunRecord? run,
        string detail)
        => new(status, run, detail);

    private static CustomLoopHumanInputCheckpointReconciliation Reconciliation(
        CustomLoopHumanInputCheckpointReconciliationStatus status,
        CustomLoopRunRecord? run,
        string detail)
        => new(status, run, detail);

}
