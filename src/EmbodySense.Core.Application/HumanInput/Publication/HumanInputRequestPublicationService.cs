using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Governance.Authority.Grants;
using EmbodySense.Core.Application.HumanInput.Lifecycle;
using EmbodySense.Core.Application.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Application.HumanInput.Publication.Models;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.HumanInput;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.HumanInput.Checkpoints;
using EmbodySense.Core.Common.Loops.HumanInput.Checkpoints.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Application.HumanInput.Publication;

/// <summary>Reconciles immutable durable Human Input checkpoints into the one canonical request lifecycle ledger.</summary>
/// <remarks>A checkpoint is always reread from the canonical run store before publication. The first Create is gated by
/// the exact retained grant through <see cref="HumanInputRequestLifecycleService"/>; an exact replay is instead proved
/// from immutable lifecycle evidence and remains available after that historical grant expires.</remarks>
public sealed class HumanInputRequestPublicationService : IHumanInputRequestPublicationService
{
    private const string PublicationReason = "human-input-checkpoint-publication";
    private const string PublicationStoreHealthRequestId = "human-input-publication-health";
    private readonly ICapabilityAuthorityTransaction _authorityTransaction;
    private readonly IAuthorityGrantResolver _grantResolver;
    private readonly IHumanInputRequestLifecycleStore _lifecycleStore;
    private readonly ICustomLoopRunStore _runs;
    private readonly TimeProvider _timeProvider;
    private readonly string _workspaceId;

    /// <summary>Creates one canonical publication boundary over run, request-lifecycle, grant, workspace, and clock ports.</summary>
    /// <param name="runs">The sole canonical custom-loop run store.</param>
    /// <param name="lifecycleStore">The sole canonical Human Input request lifecycle store.</param>
    /// <param name="grantResolver">The current exact grant resolver used for the first Create attempt.</param>
    /// <param name="authorityTransaction">The shared workspace authority transaction used by lifecycle persistence.</param>
    /// <param name="workspaceId">The server-configured canonical workspace scope.</param>
    /// <param name="timeProvider">The trusted lifecycle clock, or the system clock when omitted.</param>
    /// <exception cref="ArgumentNullException">Thrown when a required canonical dependency is null.</exception>
    /// <exception cref="ArgumentException">Thrown when the configured workspace scope is not canonical.</exception>
    public HumanInputRequestPublicationService(
        ICustomLoopRunStore runs,
        IHumanInputRequestLifecycleStore lifecycleStore,
        IAuthorityGrantResolver grantResolver,
        ICapabilityAuthorityTransaction authorityTransaction,
        string workspaceId,
        TimeProvider? timeProvider = null)
    {
        _runs = runs ?? throw new ArgumentNullException(nameof(runs));
        _lifecycleStore = lifecycleStore ?? throw new ArgumentNullException(nameof(lifecycleStore));
        _grantResolver = grantResolver ?? throw new ArgumentNullException(nameof(grantResolver));
        _authorityTransaction = authorityTransaction ?? throw new ArgumentNullException(nameof(authorityTransaction));
        if (!ContextualRoleWorkspaceId.IsValid(workspaceId)) throw new ArgumentException("Workspace id must use the canonical workspace-sha256 scope contract.", nameof(workspaceId));
        _workspaceId = workspaceId;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<HumanInputRequestPublicationHealthResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        HumanInputRequestLifecycleStoreReadResult? read;
        try
        {
            read = await _lifecycleStore.ReadAsync(PublicationStoreHealthRequestId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Health(HumanInputRequestPublicationHealthStatus.Unavailable);
        }

        if (read is null || !Enum.IsDefined(read.Status) || read.StoreGeneration < 0)
        {
            return Health(HumanInputRequestPublicationHealthStatus.Corrupt);
        }

        return read.Status switch
        {
            HumanInputRequestLifecycleStoreReadStatus.NotFound
                when read.PrimarySnapshot is null && read.RelatedSnapshot is null && read.ExistingOperation is null
                => Health(HumanInputRequestPublicationHealthStatus.Ready),
            HumanInputRequestLifecycleStoreReadStatus.Ready
                when read.PrimarySnapshot is not null && read.RelatedSnapshot is null && read.ExistingOperation is null
                => Health(HumanInputRequestPublicationHealthStatus.Ready),
            HumanInputRequestLifecycleStoreReadStatus.Unavailable
                when read.PrimarySnapshot is null && read.RelatedSnapshot is null && read.ExistingOperation is null
                => Health(HumanInputRequestPublicationHealthStatus.Unavailable),
            _ => Health(HumanInputRequestPublicationHealthStatus.Corrupt),
        };
    }

    /// <inheritdoc />
    public async Task<HumanInputRequestPublicationResult> PublishAsync(
        HumanInputRequestPublicationRequest? request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsValidRequest(request))
        {
            return Result(HumanInputRequestPublicationStatus.Corrupt);
        }

        try
        {
            return await _authorityTransaction.ExecuteAsync(
                transactionToken => PublishUnderFenceAsync(request!, transactionToken),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Result(HumanInputRequestPublicationStatus.Unavailable);
        }
    }

    private async Task<HumanInputRequestPublicationResult> PublishUnderFenceAsync(
        HumanInputRequestPublicationRequest request,
        CancellationToken cancellationToken)
    {
        CustomLoopRunRecord? run;
        try
        {
            run = await _runs.GetAsync(request.RunId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Result(HumanInputRequestPublicationStatus.Unavailable);
        }

        if (run is null)
        {
            return Result(HumanInputRequestPublicationStatus.Stale);
        }

        if (!CustomLoopRunValidator.Validate(run).IsValid)
        {
            return Result(HumanInputRequestPublicationStatus.Corrupt);
        }

        var matches = run.HumanInputWaitingCheckpoints
            .Where(item => string.Equals(item.Binding.CheckpointId, request.CheckpointId, StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        if (matches.Length == 0 || !string.Equals(matches[0].CheckpointHash, request.CheckpointHash, StringComparison.Ordinal))
        {
            return Result(HumanInputRequestPublicationStatus.Stale);
        }
        if (matches.Length != 1 || !TryValidatePublicationCandidate(run, matches[0], out var actorId))
        {
            return Result(HumanInputRequestPublicationStatus.Corrupt);
        }

        // TODO(#660): serialize loop cancellation with this cross-store Create and retire any request when cancellation wins after publication.
        var command = CreateCommand(run, matches[0]);
        if (command is null)
        {
            return Result(HumanInputRequestPublicationStatus.Corrupt);
        }

        if (matches[0].Posture != GovernedLoopHumanInputWaitingCheckpointPosture.Pending)
        {
            var retained = await HasRetainedPublicationAsync(command, cancellationToken).ConfigureAwait(false);
            if (retained is not true)
            {
                return Result(retained is null ? HumanInputRequestPublicationStatus.Unavailable : HumanInputRequestPublicationStatus.Corrupt);
            }
        }

        var adapter = run.SequentialAdapterBinding!;
        var service = new HumanInputRequestLifecycleService(
            _lifecycleStore,
            new AdmissionBoundHumanInputRequestLifecycleActorAuthorizer(
                command,
                actorId!,
                adapter.AdmissionReceipt,
                adapter.AdmissionReceiptHash,
                _workspaceId),
            _grantResolver,
            _authorityTransaction,
            _workspaceId,
            _timeProvider);
        HumanInputRequestLifecycleMutationResult mutation;
        try
        {
            mutation = await service.MutateAsync(command, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Result(HumanInputRequestPublicationStatus.Unavailable);
        }

        return mutation?.Status switch
        {
            HumanInputRequestLifecycleMutationStatus.Committed => Result(HumanInputRequestPublicationStatus.Published),
            HumanInputRequestLifecycleMutationStatus.Replayed => Result(HumanInputRequestPublicationStatus.Replayed),
            HumanInputRequestLifecycleMutationStatus.GrantUnavailable
                or HumanInputRequestLifecycleMutationStatus.Unavailable
                or HumanInputRequestLifecycleMutationStatus.Ambiguous => Result(HumanInputRequestPublicationStatus.Unavailable),
            _ => Result(HumanInputRequestPublicationStatus.Corrupt),
        };
    }

    private HumanInputRequestLifecycleCommand? CreateCommand(
        CustomLoopRunRecord run,
        GovernedLoopHumanInputWaitingCheckpoint checkpoint)
    {
        if (!AuthorityPurpose.TryParse(PublicationReason, out var reason, out _))
        {
            return null;
        }

        try
        {
            var operationId = "human-input-publication-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(
                '\n',
                "embodysense-human-input-checkpoint-publication-v1",
                run.Id,
                checkpoint.Binding.CheckpointId,
                checkpoint.Request.RequestId,
                checkpoint.Request.RequestVersionId,
                checkpoint.Request.RequestHash))));
            return HumanInputRequestLifecycleCommandHash.Apply(new HumanInputRequestLifecycleCommand(
                HumanInputRequestLifecycleCommand.CurrentSchemaVersion,
                operationId,
                HumanInputRequestLifecycleOperationKind.Create,
                checkpoint.Request.RequestId,
                0,
                HumanInputRequestLifecycleStatus.Unknown,
                null,
                null,
                checkpoint.Request,
                run.SequentialAdapterBinding!.AdmissionReceipt.Intent.AuthorityGrant,
                reason!,
                string.Empty));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private async Task<bool?> HasRetainedPublicationAsync(HumanInputRequestLifecycleCommand command, CancellationToken cancellationToken)
    {
        HumanInputRequestLifecycleStoreReadResult? read;
        try
        {
            read = await _lifecycleStore.ReadForMutationAsync(command.RequestId, command.OperationId, command.RequestHash, null, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }

        if (read is null
            || read.Status is HumanInputRequestLifecycleStoreReadStatus.Unavailable or HumanInputRequestLifecycleStoreReadStatus.Ambiguous)
        {
            return null;
        }

        return read.Status == HumanInputRequestLifecycleStoreReadStatus.Ready
            && read.ExistingOperation is
            {
                RequestId: var requestId,
                Evidence:
                {
                    OperationId: var operationId,
                    Kind: HumanInputRequestLifecycleOperationKind.Create,
                    Outcome: HumanInputRequestLifecycleOperationOutcome.Committed,
                    RequestHash: var requestHash,
                },
            }
            && string.Equals(requestId, command.RequestId, StringComparison.Ordinal)
            && string.Equals(operationId, command.OperationId, StringComparison.Ordinal)
            && string.Equals(requestHash, command.RequestHash, StringComparison.Ordinal);
    }

    private bool TryValidatePublicationCandidate(
        CustomLoopRunRecord run,
        GovernedLoopHumanInputWaitingCheckpoint checkpoint,
        out AuthorityActorId? actorId)
    {
        actorId = null;
        var adapter = run.SequentialAdapterBinding;
        var activation = run.Frontier?.Payload.Nodes.ElementAtOrDefault(checkpoint.Binding.ActivationOrdinal);
        if (adapter is null
            || !GovernedLoopHumanInputWaitingCheckpointContractValidator.Validate(checkpoint).IsValid
            || !AuthorityActorId.TryParse(run.AdmissionActor, out actorId, out _)
            || !string.Equals(adapter.WorkspaceId, _workspaceId, StringComparison.Ordinal)
            || !string.Equals(checkpoint.Binding.WorkspaceId, _workspaceId, StringComparison.Ordinal)
            || !Equals(checkpoint.Binding.Execution, adapter.ExecutionBinding)
            || !Equals(checkpoint.Binding.Publication, adapter.AdmissionReceipt.Intent.Publication)
            || !string.Equals(checkpoint.Binding.GraphArtifactHash, adapter.GraphArtifactHash, StringComparison.Ordinal)
            || !string.Equals(checkpoint.Binding.GraphLayoutHash, adapter.GraphLayoutHash, StringComparison.Ordinal)
            || !string.Equals(checkpoint.Binding.AdmissionReceiptHash, adapter.AdmissionReceiptHash, StringComparison.Ordinal)
            || !string.Equals(checkpoint.ResolvedPolicy.ActorId, run.AdmissionActor, StringComparison.Ordinal)
            || !string.Equals(checkpoint.Request.Binding.WorkspaceId, _workspaceId, StringComparison.Ordinal)
            || !string.Equals(checkpoint.Request.Binding.LoopGraphId, adapter.ExecutionBinding.Revision.GraphId, StringComparison.Ordinal)
            || !string.Equals(checkpoint.Request.Binding.LoopRevisionId, adapter.ExecutionBinding.Revision.RevisionId, StringComparison.Ordinal)
            || !string.Equals(checkpoint.Request.Binding.RunId, run.Id, StringComparison.Ordinal)
            || !string.Equals(checkpoint.Request.Binding.CheckpointId, checkpoint.Binding.CheckpointId, StringComparison.Ordinal)
            || !string.Equals(checkpoint.Request.Binding.NodeId, checkpoint.Binding.NodeId, StringComparison.Ordinal))
        {
            return false;
        }

        var pending = activation is { Status: GovernedLoopNodeExecutionStatus.Waiting, Descriptor.Kind: GovernedLoopNodeKind.HumanInput }
            && (run.Status == CustomLoopRunStatus.Waiting
                    && run.Frontier?.Payload.Status == GovernedLoopFrontierStatus.Waiting
                    && checkpoint.Posture is GovernedLoopHumanInputWaitingCheckpointPosture.Pending or GovernedLoopHumanInputWaitingCheckpointPosture.AnsweredNotResumed
                || run.Status == CustomLoopRunStatus.Running
                    && run.Frontier?.Payload.Status == GovernedLoopFrontierStatus.Active
                    && checkpoint.Posture == GovernedLoopHumanInputWaitingCheckpointPosture.Pending);
        var retired = run.Status == CustomLoopRunStatus.Running
            && run.Frontier?.Payload.Status == GovernedLoopFrontierStatus.Active
            && checkpoint.Posture is GovernedLoopHumanInputWaitingCheckpointPosture.Expired or GovernedLoopHumanInputWaitingCheckpointPosture.Rejected
            && checkpoint.Evidence.LastOrDefault()?.Kind is GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Expired or GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Rejected
            && activation is { Status: GovernedLoopNodeExecutionStatus.Failed, Descriptor.Kind: GovernedLoopNodeKind.HumanInput, OutcomeEvidenceId: not null, OutcomeEvidenceHash: not null };
        return pending || retired;
    }

    private static bool IsValidRequest(HumanInputRequestPublicationRequest? request)
        => request is not null
            && CustomLoopArtifactIdentifier.IsValid(request.RunId)
            && HumanInputIdentifier.IsValid(request.CheckpointId)
            && IsSha256(request.CheckpointHash);

    private static bool IsSha256(string? value)
        => value is { Length: 64 }
            && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static HumanInputRequestPublicationResult Result(HumanInputRequestPublicationStatus status) => new(status);

    private static HumanInputRequestPublicationHealthResult Health(HumanInputRequestPublicationHealthStatus status) => new(status);
}
