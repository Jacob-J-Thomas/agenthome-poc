using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Application.ContextualRoles.Models;
using EmbodySense.Core.Application.Governance.Authority.Grants;
using EmbodySense.Core.Application.Governance.Authority.Grants.Models;
using EmbodySense.Core.Application.Loops.Admission.Models;
using EmbodySense.Core.Application.Loops.GraphAuthoring;
using EmbodySense.Core.Application.Loops.GraphAuthoring.Models;
using EmbodySense.Core.Application.Loops.Revisions.Models;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Loops.Admission;
using EmbodySense.Core.Common.Loops.Admission.Models;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Application.Loops.Admission;

/// <summary>Atomically proves and records exact governed-loop admission under one workspace authority fence.</summary>
public sealed class GovernedLoopAdmissionService : IGovernedLoopAdmissionService
{
    private const int MaximumCommitAttempts = 3;
    private readonly string _workspaceId;
    private readonly IGovernedLoopAdmissionStore _store;
    private readonly IGovernedLoopGraphRevisionStore _graphStore;
    private readonly IGovernedLoopGrantBindingSource _bindingSource;
    private readonly IAuthorityGrantRoleSource _roleSource;
    private readonly IAuthorityGrantResolver _grantResolver;
    private readonly ICapabilityAdmissionService _capabilityAdmissionService;
    private readonly ICapabilityAuthorityTransaction _authorityTransaction;
    private readonly IGovernedLoopAdmissionRunIdentityGenerator _runIdentityGenerator;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a workspace-bound admission service over surface-neutral Application ports.</summary>
    public GovernedLoopAdmissionService(
        string workspaceId,
        IGovernedLoopAdmissionStore store,
        IGovernedLoopGraphRevisionStore graphStore,
        IGovernedLoopGrantBindingSource bindingSource,
        IAuthorityGrantRoleSource roleSource,
        IAuthorityGrantResolver grantResolver,
        ICapabilityAdmissionService capabilityAdmissionService,
        ICapabilityAuthorityTransaction authorityTransaction,
        IGovernedLoopAdmissionRunIdentityGenerator runIdentityGenerator,
        TimeProvider? timeProvider = null)
    {
        if (!ContextualRoleWorkspaceId.IsValid(workspaceId))
        {
            throw new ArgumentException("Workspace id must use the canonical workspace-sha256 scope contract.", nameof(workspaceId));
        }

        _workspaceId = workspaceId;
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _graphStore = graphStore ?? throw new ArgumentNullException(nameof(graphStore));
        _bindingSource = bindingSource ?? throw new ArgumentNullException(nameof(bindingSource));
        _roleSource = roleSource ?? throw new ArgumentNullException(nameof(roleSource));
        _grantResolver = grantResolver ?? throw new ArgumentNullException(nameof(grantResolver));
        _capabilityAdmissionService = capabilityAdmissionService ?? throw new ArgumentNullException(nameof(capabilityAdmissionService));
        _authorityTransaction = authorityTransaction ?? throw new ArgumentNullException(nameof(authorityTransaction));
        _runIdentityGenerator = runIdentityGenerator ?? throw new ArgumentNullException(nameof(runIdentityGenerator));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<GovernedLoopAdmissionResult> AdmitAsync(
        GovernedLoopAdmissionRequest? request,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidRequest(request))
        {
            return Result(GovernedLoopAdmissionStatus.Invalid, request);
        }

        GovernedLoopAdmissionResult? completed = null;
        try
        {
            return await _authorityTransaction.ExecuteAsync(
                async transactionToken =>
                {
                    completed = await AdmitUnderFenceAsync(request!, transactionToken).ConfigureAwait(false);
                    return completed;
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && completed is null)
        {
            throw;
        }
        catch (Exception)
        {
            return completed is not null && HasDurableProof(completed)
                ? completed
                : Result(completed is null ? GovernedLoopAdmissionStatus.Unavailable : GovernedLoopAdmissionStatus.Ambiguous, request);
        }
    }

    private async Task<GovernedLoopAdmissionResult> AdmitUnderFenceAsync(
        GovernedLoopAdmissionRequest request,
        CancellationToken cancellationToken)
    {
        var read = await ReadStoreAsync(request, cancellationToken).ConfigureAwait(false);
        var readDisposition = ClassifyRead(request, read);
        if (readDisposition is not null)
        {
            if (read?.Status == GovernedLoopAdmissionStoreReadStatus.Recoverable
                && readDisposition.Status == GovernedLoopAdmissionStatus.Replayed
                && read.Outcome is not null)
            {
                return await CommitOutcomeAsync(
                    request,
                    read.Outcome,
                    read.StoreGeneration,
                    GovernedLoopAdmissionStatus.Replayed,
                    honorCallerCancellation: false,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            return readDisposition;
        }

        var storeGeneration = read!.StoreGeneration;
        var artifactRead = await ReadArtifactAsync(request, cancellationToken).ConfigureAwait(false);
        if (artifactRead.Status != GovernedLoopRevisionStoreReadStatus.Ready)
        {
            return artifactRead.Status is GovernedLoopRevisionStoreReadStatus.NotFound or GovernedLoopRevisionStoreReadStatus.Unavailable
                ? Result(GovernedLoopAdmissionStatus.Unavailable, request)
                : Result(GovernedLoopAdmissionStatus.Ambiguous, request);
        }

        if (!TryValidateArtifact(artifactRead, request.Publication.Revision, out var artifact))
        {
            return Result(GovernedLoopAdmissionStatus.Ambiguous, request);
        }

        var intent = new GovernedLoopAdmissionIntent(
            GovernedLoopAdmissionIntent.CurrentSchemaVersion,
            _workspaceId,
            request.OperationId,
            request.RequestHash,
            request.Publication,
            request.AuthorityGrant,
            artifact!.Graph.OwningRole,
            request.ActorId,
            request.Surface,
            artifact.ArtifactHash,
            artifact.LayoutHash);
        if (!GovernedLoopAdmissionValidator.Validate(intent).IsValid)
        {
            return Result(GovernedLoopAdmissionStatus.Ambiguous, request);
        }

        var binding = await ResolveBindingAsync(request, cancellationToken).ConfigureAwait(false);
        if (binding.Status != AuthorityGrantDependencyStatus.Active)
        {
            return MapBindingFailure(request, binding.Status);
        }

        if (!IsExactBinding(binding, request.Publication, artifact, intent.Role))
        {
            return Result(GovernedLoopAdmissionStatus.Ambiguous, request);
        }

        var role = await ResolveRoleAsync(intent.Role, cancellationToken).ConfigureAwait(false);
        if (role.Status != AuthorityGrantDependencyStatus.Active)
        {
            return MapRoleFailure(request, intent, role, storeGeneration);
        }

        if (!IsExactActiveRole(role, intent.Role))
        {
            return Result(GovernedLoopAdmissionStatus.Ambiguous, request);
        }

        var grant = await ResolveGrantAsync(request.AuthorityGrant, cancellationToken).ConfigureAwait(false);
        if (grant.Status != AuthorityGrantResolutionStatus.Active)
        {
            return MapGrantFailure(request, intent, grant.Status, storeGeneration);
        }

        if (!IsExactActiveGrant(grant, request.AuthorityGrant, intent.Role, request.Publication))
        {
            return Result(GovernedLoopAdmissionStatus.Ambiguous, request);
        }

        if (role.Lifecycle!.UpdatedAtUtc > grant.EvaluatedAtUtc
            || role.Revision!.Provenance.RecordedAtUtc > grant.EvaluatedAtUtc)
        {
            return Result(GovernedLoopAdmissionStatus.Ambiguous, request);
        }

        if (!TryBuildCapabilityManifest(artifact, out var manifest, out var requirementsHash))
        {
            return Result(GovernedLoopAdmissionStatus.Ambiguous, request);
        }

        var graphIds = artifact.Graph.AuthorityCeiling.CapabilityIds.ToHashSet(StringComparer.Ordinal);
        var roleIds = role.Revision!.PolicyMaxima.CapabilityIds.ToHashSet(StringComparer.Ordinal);
        var grantIds = grant.EffectiveCeiling.Capabilities.Select(item => item.Id.Value).ToHashSet(StringComparer.Ordinal);
        var allowedIds = new List<CapabilityId>(graphIds.Count);
        foreach (var id in graphIds.Order(StringComparer.Ordinal))
        {
            if (!roleIds.Contains(id) || !grantIds.Contains(id) || !CapabilityId.TryParse(id, out var parsed, out _))
            {
                return DefinitiveFailure(request, intent, GovernedLoopAdmissionFailureCode.CapabilityResolutionDenied, storeGeneration);
            }

            allowedIds.Add(parsed!);
        }

        DateTimeOffset evaluatedAtUtc;
        CapabilityAdmissionSnapshot capabilitySnapshot;
        if (allowedIds.Count == 0)
        {
            if (!TryGetTrustedUtcNow(out evaluatedAtUtc))
            {
                return Result(GovernedLoopAdmissionStatus.Unavailable, request);
            }

            capabilitySnapshot = new CapabilityAdmissionSnapshot(
                CapabilityAdmissionSnapshot.CurrentSchemaVersion,
                _workspaceId,
                manifest!,
                requirementsHash!,
                [],
                [],
                evaluatedAtUtc);
        }
        else
        {
            var capability = await AdmitCapabilitiesAsync(manifest!, allowedIds, cancellationToken).ConfigureAwait(false);
            if (!capability.IsAdmitted)
            {
                return Result(GovernedLoopAdmissionStatus.Unavailable, request);
            }

            if (capability.Snapshot is null || !TryGetTrustedUtcNow(out evaluatedAtUtc))
            {
                return Result(capability.Snapshot is null ? GovernedLoopAdmissionStatus.Ambiguous : GovernedLoopAdmissionStatus.Unavailable, request);
            }

            capabilitySnapshot = capability.Snapshot;
        }

        if (!IsExactCapabilitySnapshot(capabilitySnapshot, manifest!, requirementsHash!, graphIds, evaluatedAtUtc))
        {
            return Result(GovernedLoopAdmissionStatus.Ambiguous, request);
        }

        if (!IsTrustedUtc(grant.EvaluatedAtUtc)
            || grant.EvaluatedAtUtc > evaluatedAtUtc
            || capabilitySnapshot.AdmittedAtUtc > evaluatedAtUtc)
        {
            return Result(GovernedLoopAdmissionStatus.Ambiguous, request);
        }

        var exactPins = capabilitySnapshot.Pins.Select(item => item.DescriptorIdentity).ToHashSet();
        var effectiveCapabilities = grant.EffectiveCeiling.Capabilities
            .Where(item => roleIds.Contains(item.Id.Value) && graphIds.Contains(item.Id.Value) && exactPins.Contains(item))
            .ToArray();
        if (effectiveCapabilities.Select(item => item.Id.Value).ToHashSet(StringComparer.Ordinal).Count != graphIds.Count)
        {
            return DefinitiveFailure(request, intent, GovernedLoopAdmissionFailureCode.CapabilityResolutionDenied, storeGeneration);
        }

        var effectiveAuthority = new AuthorityCeiling(
            effectiveCapabilities,
            grant.EffectiveCeiling.DataClasses,
            grant.EffectiveCeiling.MaxTargetCount,
            grant.EffectiveCeiling.MaxSideEffectClass,
            grant.EffectiveCeiling.AllowsRecurrence,
            grant.EffectiveCeiling.AllowsExternalPublication,
            grant.EffectiveCeiling.AllowsIrreversibleAction);
        if (!AuthorityProfileValidator.ValidateCeiling(effectiveAuthority).IsValid)
        {
            return Result(GovernedLoopAdmissionStatus.Ambiguous, request);
        }

        GovernedLoopExecutionBinding executionBinding;
        try
        {
            executionBinding = GovernedLoopExecutionBinding.Create(
                1,
                _runIdentityGenerator.CreateRunId(),
                request.Publication.Revision,
                1);
        }
        catch (Exception)
        {
            return Result(GovernedLoopAdmissionStatus.Unavailable, request);
        }

        GovernedLoopAdmissionTerminalOutcome outcome;
        try
        {
            var evidence = GovernedLoopAdmissionContractHash.Apply(new GovernedLoopAdmissionEvidence(
                GovernedLoopAdmissionEvidence.CurrentSchemaVersion,
                GovernedLoopAdmissionContractHash.ComputeIntentHash(intent),
                executionBinding,
                effectiveAuthority,
                capabilitySnapshot,
                GovernedLoopAdmissionContractHash.CreateEvidenceReferences(intent, effectiveAuthority, capabilitySnapshot),
                evaluatedAtUtc,
                string.Empty));
            var receipt = GovernedLoopAdmissionContractHash.Apply(new GovernedLoopAdmissionReceipt(
                GovernedLoopAdmissionReceipt.CurrentSchemaVersion,
                intent,
                evidence,
                evaluatedAtUtc,
                string.Empty));
            outcome = GovernedLoopAdmissionContractHash.Apply(new GovernedLoopAdmissionTerminalOutcome(
                GovernedLoopAdmissionTerminalOutcome.CurrentSchemaVersion,
                intent,
                GovernedLoopAdmissionDisposition.Admitted,
                receipt,
                null,
                evaluatedAtUtc,
                string.Empty));
        }
        catch (ArgumentException)
        {
            return Result(GovernedLoopAdmissionStatus.Ambiguous, request);
        }

        if (!GovernedLoopAdmissionValidator.Validate(outcome).IsValid)
        {
            return Result(GovernedLoopAdmissionStatus.Ambiguous, request);
        }

        return await CommitOutcomeAsync(
            request,
            outcome,
            storeGeneration,
            GovernedLoopAdmissionStatus.Admitted,
            honorCallerCancellation: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task<GovernedLoopAdmissionResult> CommitOutcomeAsync(
        GovernedLoopAdmissionRequest request,
        GovernedLoopAdmissionTerminalOutcome outcome,
        long expectedGeneration,
        GovernedLoopAdmissionStatus committedStatus,
        bool honorCallerCancellation,
        CancellationToken cancellationToken,
        int remainingRecoveryFinalizations = 1)
    {
        var intentHash = GovernedLoopAdmissionContractHash.ComputeIntentHash(outcome.Intent);
        for (var attempt = 0; attempt < MaximumCommitAttempts; attempt++)
        {
            if (attempt == 0 && honorCallerCancellation)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            GovernedLoopAdmissionStoreCommitResult? commit;
            try
            {
                commit = await _store.CommitAsync(
                    new GovernedLoopAdmissionStoreMutation(
                        _workspaceId,
                        request.OperationId,
                        request.RequestHash,
                        intentHash,
                        expectedGeneration,
                        outcome),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return await RecoverUncertainCommitAsync(request, outcome, remainingRecoveryFinalizations).ConfigureAwait(false);
            }

            if (commit is null || !Enum.IsDefined(commit.Status) || commit.Status == GovernedLoopAdmissionStoreCommitStatus.Unknown || commit.StoreGeneration < 0)
            {
                return await RecoverUncertainCommitAsync(request, outcome, remainingRecoveryFinalizations).ConfigureAwait(false);
            }

            switch (commit.Status)
            {
                case GovernedLoopAdmissionStoreCommitStatus.Committed:
                    return IsDirectSuccessor(expectedGeneration, commit.StoreGeneration) && SameOutcome(commit.Outcome, outcome)
                        ? Result(committedStatus, request, outcome)
                        : Result(GovernedLoopAdmissionStatus.Ambiguous, request);
                case GovernedLoopAdmissionStoreCommitStatus.AlreadyCommitted:
                    return commit.StoreGeneration > 0
                        ? ClassifyCommittedOutcome(request, commit.Outcome)
                        : Result(GovernedLoopAdmissionStatus.Ambiguous, request);
                case GovernedLoopAdmissionStoreCommitStatus.OperationConflict:
                    return commit.Outcome is null
                        ? Result(GovernedLoopAdmissionStatus.Conflict, request)
                        : ClassifyCommittedOutcome(request, commit.Outcome);
                case GovernedLoopAdmissionStoreCommitStatus.GenerationConflict:
                    if (commit.Outcome is not null || commit.StoreGeneration <= expectedGeneration)
                    {
                        return Result(GovernedLoopAdmissionStatus.Ambiguous, request);
                    }

                    var reread = await ReadStoreAsync(request, CancellationToken.None).ConfigureAwait(false);
                    var disposition = ClassifyRead(request, reread);
                    if (disposition is not null)
                    {
                        if (reread?.Status == GovernedLoopAdmissionStoreReadStatus.Recoverable
                            && disposition.Status == GovernedLoopAdmissionStatus.Replayed
                            && reread.Outcome is not null)
                        {
                            outcome = reread.Outcome;
                            intentHash = GovernedLoopAdmissionContractHash.ComputeIntentHash(outcome.Intent);
                            expectedGeneration = reread.StoreGeneration;
                            committedStatus = GovernedLoopAdmissionStatus.Replayed;
                            continue;
                        }

                        return disposition;
                    }

                    expectedGeneration = reread!.StoreGeneration;
                    break;
                case GovernedLoopAdmissionStoreCommitStatus.LimitExceeded:
                    return commit.Outcome is null
                        ? Result(GovernedLoopAdmissionStatus.LimitExceeded, request)
                        : Result(GovernedLoopAdmissionStatus.Ambiguous, request);
                case GovernedLoopAdmissionStoreCommitStatus.Unavailable:
                    return commit.Outcome is null
                        ? Result(GovernedLoopAdmissionStatus.Unavailable, request)
                        : Result(GovernedLoopAdmissionStatus.Ambiguous, request);
                case GovernedLoopAdmissionStoreCommitStatus.Ambiguous:
                default:
                    return await RecoverUncertainCommitAsync(request, outcome, remainingRecoveryFinalizations).ConfigureAwait(false);
            }
        }

        return Result(GovernedLoopAdmissionStatus.Conflict, request);
    }

    private async Task<GovernedLoopAdmissionResult> RecoverUncertainCommitAsync(
        GovernedLoopAdmissionRequest request,
        GovernedLoopAdmissionTerminalOutcome proposed,
        int remainingRecoveryFinalizations)
    {
        var read = await ReadStoreAsync(request, CancellationToken.None).ConfigureAwait(false);
        var disposition = ClassifyRead(request, read);
        if (disposition is null)
        {
            return Result(GovernedLoopAdmissionStatus.Ambiguous, request);
        }

        if (read?.Status == GovernedLoopAdmissionStoreReadStatus.Recoverable
            && disposition.Status == GovernedLoopAdmissionStatus.Replayed
            && read.Outcome is not null
            && SameOutcome(read.Outcome, proposed)
            && remainingRecoveryFinalizations > 0)
        {
            return await CommitOutcomeAsync(
                request,
                read.Outcome,
                read.StoreGeneration,
                GovernedLoopAdmissionStatus.Replayed,
                honorCallerCancellation: false,
                cancellationToken: CancellationToken.None,
                remainingRecoveryFinalizations: remainingRecoveryFinalizations - 1).ConfigureAwait(false);
        }

        return disposition.Status == GovernedLoopAdmissionStatus.Unavailable
            || read?.Status == GovernedLoopAdmissionStoreReadStatus.Recoverable
            ? Result(GovernedLoopAdmissionStatus.Ambiguous, request)
            : disposition;
    }

    private async Task<GovernedLoopAdmissionStoreReadResult?> ReadStoreAsync(
        GovernedLoopAdmissionRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _store.ReadByOperationAsync(_workspaceId, request.OperationId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new GovernedLoopAdmissionStoreReadResult(GovernedLoopAdmissionStoreReadStatus.Unavailable, 0, null);
        }
    }

    private GovernedLoopAdmissionResult? ClassifyRead(
        GovernedLoopAdmissionRequest request,
        GovernedLoopAdmissionStoreReadResult? read)
    {
        if (read is null || read.StoreGeneration < 0 || !Enum.IsDefined(read.Status) || read.Status == GovernedLoopAdmissionStoreReadStatus.Unknown)
        {
            return Result(GovernedLoopAdmissionStatus.Ambiguous, request);
        }

        return read.Status switch
        {
            GovernedLoopAdmissionStoreReadStatus.NotFound when read.Outcome is null => null,
            GovernedLoopAdmissionStoreReadStatus.Found when read.StoreGeneration > 0 && read.Outcome is not null => ClassifyCommittedOutcome(request, read.Outcome),
            GovernedLoopAdmissionStoreReadStatus.Recoverable when read.StoreGeneration > 0 && read.Outcome is not null => ClassifyCommittedOutcome(request, read.Outcome),
            GovernedLoopAdmissionStoreReadStatus.Unavailable when read.Outcome is null => Result(GovernedLoopAdmissionStatus.Unavailable, request),
            GovernedLoopAdmissionStoreReadStatus.Ambiguous when read.Outcome is null => Result(GovernedLoopAdmissionStatus.Ambiguous, request),
            _ => Result(GovernedLoopAdmissionStatus.Ambiguous, request),
        };
    }

    private GovernedLoopAdmissionResult ClassifyCommittedOutcome(
        GovernedLoopAdmissionRequest request,
        GovernedLoopAdmissionTerminalOutcome? outcome)
    {
        if (outcome is null
            || !IsValidTerminalOutcome(outcome)
            || !string.Equals(outcome.Intent.WorkspaceId, _workspaceId, StringComparison.Ordinal)
            || !string.Equals(outcome.Intent.OperationId, request.OperationId, StringComparison.Ordinal))
        {
            return Result(GovernedLoopAdmissionStatus.Ambiguous, request);
        }

        return SameStableRequest(request, outcome.Intent)
            ? Result(GovernedLoopAdmissionStatus.Replayed, request, outcome)
            : Result(GovernedLoopAdmissionStatus.Conflict, request);
    }

    private async Task<GovernedLoopGraphRevisionArtifactReadResult> ReadArtifactAsync(
        GovernedLoopAdmissionRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _graphStore.ReadArtifactAsync(request.Publication.Revision, cancellationToken).ConfigureAwait(false);
            return result ?? new GovernedLoopGraphRevisionArtifactReadResult(GovernedLoopRevisionStoreReadStatus.Ambiguous, 0, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new GovernedLoopGraphRevisionArtifactReadResult(GovernedLoopRevisionStoreReadStatus.Unavailable, 0, null);
        }
    }

    private async Task<GovernedLoopGrantBindingResolution> ResolveBindingAsync(
        GovernedLoopAdmissionRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _bindingSource.ResolveAsync(request.Publication, cancellationToken).ConfigureAwait(false)
                ?? EmptyBinding(AuthorityGrantDependencyStatus.Ambiguous);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return EmptyBinding(AuthorityGrantDependencyStatus.Unavailable);
        }
    }

    private async Task<AuthorityGrantRoleResolution> ResolveRoleAsync(
        ContextualRoleRevisionPin pin,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _roleSource.ResolveAsync(pin, cancellationToken).ConfigureAwait(false)
                ?? EmptyRole(AuthorityGrantDependencyStatus.Ambiguous);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return EmptyRole(AuthorityGrantDependencyStatus.Unavailable);
        }
    }

    private async Task<AuthorityGrantResolution> ResolveGrantAsync(
        AuthorityGrantReference reference,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _grantResolver.ResolveAsync(reference, cancellationToken).ConfigureAwait(false)
                ?? EmptyGrant(AuthorityGrantResolutionStatus.Ambiguous);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return EmptyGrant(AuthorityGrantResolutionStatus.Unavailable);
        }
    }

    private async Task<CapabilityAdmissionResult> AdmitCapabilitiesAsync(
        CapabilityDependencyManifest manifest,
        IReadOnlyCollection<CapabilityId> allowedIds,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _capabilityAdmissionService.AdmitAsync(manifest, allowedIds, cancellationToken).ConfigureAwait(false)
                ?? new CapabilityAdmissionResult(false, null, string.Empty);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new CapabilityAdmissionResult(false, null, string.Empty);
        }
    }

    private GovernedLoopAdmissionResult MapBindingFailure(
        GovernedLoopAdmissionRequest request,
        AuthorityGrantDependencyStatus status)
        => status switch
        {
            AuthorityGrantDependencyStatus.Disabled or AuthorityGrantDependencyStatus.Expired or AuthorityGrantDependencyStatus.Stale or AuthorityGrantDependencyStatus.NotFound
                => Result(GovernedLoopAdmissionStatus.Unavailable, request),
            AuthorityGrantDependencyStatus.Unavailable => Result(GovernedLoopAdmissionStatus.Unavailable, request),
            _ => Result(GovernedLoopAdmissionStatus.Ambiguous, request),
        };

    private GovernedLoopAdmissionResult MapRoleFailure(
        GovernedLoopAdmissionRequest request,
        GovernedLoopAdmissionIntent intent,
        AuthorityGrantRoleResolution role,
        long storeGeneration)
    {
        var code = role.Status switch
        {
            AuthorityGrantDependencyStatus.Stale => GovernedLoopAdmissionFailureCode.RoleReplaced,
            AuthorityGrantDependencyStatus.NotFound => GovernedLoopAdmissionFailureCode.RoleNotFound,
            AuthorityGrantDependencyStatus.Disabled when role.SourceStatus == ContextualRoleInstructionSourceProbeStatus.WorkspaceMismatch => GovernedLoopAdmissionFailureCode.RoleWorkspaceMismatch,
            AuthorityGrantDependencyStatus.Disabled when role.SourceStatus is ContextualRoleInstructionSourceProbeStatus.Missing
                or ContextualRoleInstructionSourceProbeStatus.Unsupported
                or ContextualRoleInstructionSourceProbeStatus.Oversized
                or ContextualRoleInstructionSourceProbeStatus.Substituted => GovernedLoopAdmissionFailureCode.RoleSourceMismatch,
            AuthorityGrantDependencyStatus.Disabled or AuthorityGrantDependencyStatus.Expired => GovernedLoopAdmissionFailureCode.RoleInactive,
            _ => GovernedLoopAdmissionFailureCode.None,
        };
        if (code != GovernedLoopAdmissionFailureCode.None)
        {
            return DefinitiveFailure(request, intent, code, storeGeneration);
        }

        return role.Status == AuthorityGrantDependencyStatus.Unavailable
            ? Result(GovernedLoopAdmissionStatus.Unavailable, request)
            : Result(GovernedLoopAdmissionStatus.Ambiguous, request);
    }

    private GovernedLoopAdmissionResult MapGrantFailure(
        GovernedLoopAdmissionRequest request,
        GovernedLoopAdmissionIntent intent,
        AuthorityGrantResolutionStatus status,
        long storeGeneration)
    {
        var code = status switch
        {
            AuthorityGrantResolutionStatus.NotEffective or AuthorityGrantResolutionStatus.Suspended or AuthorityGrantResolutionStatus.Revoked or AuthorityGrantResolutionStatus.Expired
                => GovernedLoopAdmissionFailureCode.GrantInactive,
            AuthorityGrantResolutionStatus.Stale or AuthorityGrantResolutionStatus.NotFound
                => GovernedLoopAdmissionFailureCode.GrantMismatch,
            AuthorityGrantResolutionStatus.CeilingExceeded
                => GovernedLoopAdmissionFailureCode.AuthorityDenied,
            _ => GovernedLoopAdmissionFailureCode.None,
        };
        if (code != GovernedLoopAdmissionFailureCode.None)
        {
            return DefinitiveFailure(request, intent, code, storeGeneration);
        }

        return status is AuthorityGrantResolutionStatus.Unavailable
            or AuthorityGrantResolutionStatus.ProfileUnavailable
            or AuthorityGrantResolutionStatus.RoleUnavailable
            or AuthorityGrantResolutionStatus.LoopUnavailable
            ? Result(GovernedLoopAdmissionStatus.Unavailable, request)
            : Result(GovernedLoopAdmissionStatus.Ambiguous, request);
    }

    private static GovernedLoopAdmissionResult DefinitiveFailure(
        GovernedLoopAdmissionRequest request,
        GovernedLoopAdmissionIntent intent,
        GovernedLoopAdmissionFailureCode failureCode,
        long storeGeneration)
    {
        _ = intent;
        _ = failureCode;
        _ = storeGeneration;
        return Result(GovernedLoopAdmissionStatus.Unavailable, request);
    }

    private static bool IsValidRequest(GovernedLoopAdmissionRequest? request)
    {
        return request is not null
            && request.SchemaVersion == GovernedLoopAdmissionRequest.CurrentSchemaVersion
            && IsToken(request.OperationId, GovernedLoopAdmissionLimits.MaxIdentifierCharacters)
            && IsSha256(request.InvocationPayloadHash)
            && GovernedLoopAdmissionRequestHash.Matches(request)
            && GovernedLoopRevisionContractValidator.Validate(request.Publication).IsValid
            && request.AuthorityGrant?.GrantId is not null
            && request.AuthorityGrant.Revision is not null
            && IsOciSha256(request.AuthorityGrant.ContentHash)
            && request.ActorId is not null
            && AuthorityActorId.TryParse(request.ActorId.Value, out var actor, out _)
            && request.ActorId.Equals(actor)
            && IsToken(request.Surface, GovernedLoopAdmissionLimits.MaxSurfaceCharacters);
    }

    private static bool TryValidateArtifact(
        GovernedLoopGraphRevisionArtifactReadResult read,
        GovernedLoopRevisionReference expectedRevision,
        out GovernedLoopGraphRevisionArtifact? artifact)
    {
        artifact = null;
        if (read.StoreGeneration < 1 || read.Artifact is null || !Equals(read.Artifact.RevisionArtifact.Revision, expectedRevision))
        {
            return false;
        }

        try
        {
            if (!string.Equals(GovernedLoopGraphRevisionContractHash.ComputeArtifactHash(read.Artifact), read.Artifact.ArtifactHash, StringComparison.Ordinal))
            {
                return false;
            }
        }
        catch (Exception)
        {
            return false;
        }

        artifact = read.Artifact;
        return true;
    }

    private static bool IsExactBinding(
        GovernedLoopGrantBindingResolution binding,
        GovernedLoopRevisionPublicationPin publication,
        GovernedLoopGraphRevisionArtifact artifact,
        ContextualRoleRevisionPin role)
    {
        try
        {
            return Equals(binding.PublicationPin, publication)
                && binding.Artifact is not null
                && Equals(binding.Artifact.RevisionArtifact.Revision, artifact.RevisionArtifact.Revision)
                && string.Equals(binding.Artifact.ArtifactHash, artifact.ArtifactHash, StringComparison.Ordinal)
                && string.Equals(binding.Artifact.LayoutHash, artifact.LayoutHash, StringComparison.Ordinal)
                && string.Equals(
                    GovernedLoopGraphRevisionContractHash.ComputeArtifactHash(binding.Artifact),
                    binding.Artifact.ArtifactHash,
                    StringComparison.Ordinal)
                && Equals(binding.OwningRole, role)
                && binding.CapabilityIds is not null
                && binding.CapabilityIds.SequenceEqual(artifact.Graph.AuthorityCeiling.CapabilityIds, StringComparer.Ordinal)
                && IsSha256(binding.EvidenceHash);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private bool IsExactActiveRole(AuthorityGrantRoleResolution role, ContextualRoleRevisionPin pin)
    {
        return Equals(role.RequestedPin, pin)
            && role.Revision is not null
            && ContextualRoleRevisionValidator.Validate(role.Revision).IsValid
            && Equals(role.Revision.Identity, pin.Identity)
            && string.Equals(role.Revision.ContentHash, pin.ContentHash, StringComparison.Ordinal)
            && role.Revision.Status == ContextualRoleStatus.Published
            && role.Revision.WorkspaceApplicability.AppliesTo(_workspaceId)
            && role.Lifecycle is
            {
                SchemaVersion: 1,
                State: ContextualRoleLifecycleState.Active,
                CurrentIdentity: not null,
            }
            && Equals(role.Lifecycle.CurrentIdentity, pin.Identity)
            && string.Equals(role.Lifecycle.RoleId, pin.Identity.RoleId, StringComparison.Ordinal)
            && ContextualRoleId.IsValid(role.Lifecycle.LastOperationId)
            && Enum.IsDefined(role.Lifecycle.LastMutationKind)
            && role.Lifecycle.LastMutationKind != ContextualRoleRevisionMutationKind.Unknown
            && IsTrustedUtc(role.Lifecycle.UpdatedAtUtc)
            && string.Equals(role.WorkspaceId, _workspaceId, StringComparison.Ordinal)
            && role.SourceStatus == ContextualRoleInstructionSourceProbeStatus.Ready
            && IsSha256(role.EvidenceHash);
    }

    private static bool IsExactActiveGrant(
        AuthorityGrantResolution resolution,
        AuthorityGrantReference reference,
        ContextualRoleRevisionPin role,
        GovernedLoopRevisionPublicationPin publication)
    {
        return Equals(resolution.RequestedReference, reference)
            && resolution.Grant is { Status: AuthorityGrantLifecycleStatus.Active } grant
            && AuthorityGrantHash.Matches(grant)
            && Equals(grant.GrantId, reference.GrantId)
            && Equals(grant.Revision, reference.Revision)
            && string.Equals(grant.ContentHash, reference.ContentHash, StringComparison.Ordinal)
            && Equals(grant.Binding.Role, role)
            && Equals(grant.Binding.Loop, publication)
            && AuthorityProfileValidator.ValidateCeiling(resolution.EffectiveCeiling).IsValid
            && AuthorityCeilingSubset.IsEqual(resolution.EffectiveCeiling, grant.RequestedCeiling)
            && IsSha256(resolution.DependencyEvidenceHash)
            && IsTrustedUtc(resolution.EvaluatedAtUtc);
    }

    private static bool TryBuildCapabilityManifest(
        GovernedLoopGraphRevisionArtifact artifact,
        out CapabilityDependencyManifest? manifest,
        out string? requirementsHash)
    {
        manifest = null;
        requirementsHash = null;
        if (!IsSha256(artifact.ArtifactHash)
            || !CapabilityId.TryParse("org.embodysense/loop-" + artifact.ArtifactHash[..32], out var subject, out _)
            || !CapabilityVersionRange.TryParse("*", out var any, out _)
            || !CapabilityIntegrityDigest.TryParse("sha256:" + artifact.ArtifactHash, out var checksum, out _))
        {
            return false;
        }

        var required = new List<CapabilityDependency>(artifact.Graph.AuthorityCeiling.CapabilityIds.Count);
        foreach (var value in artifact.Graph.AuthorityCeiling.CapabilityIds.Order(StringComparer.Ordinal))
        {
            if (!CapabilityId.TryParse(value, out var id, out _))
            {
                return false;
            }

            required.Add(new CapabilityDependency(id!, any!));
        }

        var candidate = new CapabilityDependencyManifest(
            CapabilityDependencyManifest.CurrentSchemaVersion,
            CapabilityDependencyManifestKind.LoopPackage,
            subject!,
            required,
            [],
            new CapabilityDependencyArtifactMetadata(checksum, null));
        if (!CapabilityDependencyManifestHash.TryCompute(candidate, out var hash, out _))
        {
            return false;
        }

        manifest = candidate;
        requirementsHash = hash!.Value;
        return true;
    }

    private bool IsExactCapabilitySnapshot(
        CapabilityAdmissionSnapshot snapshot,
        CapabilityDependencyManifest requirements,
        string requirementsHash,
        IReadOnlySet<string> graphIds,
        DateTimeOffset evaluatedAtUtc)
    {
        try
        {
            if (CapabilityAdmissionSnapshotValidator.Validate(snapshot) is not null
                || !string.Equals(snapshot.WorkspaceScopeId, _workspaceId, StringComparison.Ordinal)
                || !string.Equals(snapshot.RequirementsHash, requirementsHash, StringComparison.Ordinal)
                || !CapabilityDependencyManifestHash.TryCompute(snapshot.Requirements, out var actualHash, out _)
                || !string.Equals(actualHash!.Value, requirementsHash, StringComparison.Ordinal)
                || snapshot.AdmittedAtUtc > evaluatedAtUtc)
            {
                return false;
            }

            var rootSelected = snapshot.Evidence
                .Where(item => item?.SubjectId is not null && item.SubjectId.Equals(requirements.SubjectId) && string.Equals(item.Outcome, "Selected", StringComparison.Ordinal))
                .Select(item => item.DependencyId.Value)
                .ToHashSet(StringComparer.Ordinal);
            return rootSelected.SetEquals(graphIds);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private bool TryGetTrustedUtcNow(out DateTimeOffset utcNow)
    {
        try
        {
            utcNow = _timeProvider.GetUtcNow();
            return IsTrustedUtc(utcNow);
        }
        catch (Exception)
        {
            utcNow = default;
            return false;
        }
    }

    private static bool SameStableRequest(GovernedLoopAdmissionRequest request, GovernedLoopAdmissionIntent intent)
    {
        return string.Equals(intent.RequestHash, request.RequestHash, StringComparison.Ordinal)
            && Equals(intent.Publication, request.Publication)
            && Equals(intent.AuthorityGrant, request.AuthorityGrant)
            && Equals(intent.ActorId, request.ActorId)
            && string.Equals(intent.Surface, request.Surface, StringComparison.Ordinal);
    }

    private static bool SameOutcome(GovernedLoopAdmissionTerminalOutcome? left, GovernedLoopAdmissionTerminalOutcome right)
        => left is not null
            && IsValidTerminalOutcome(left)
            && IsValidTerminalOutcome(right)
            && string.Equals(left.ContentHash, right.ContentHash, StringComparison.Ordinal);

    private static bool IsDirectSuccessor(long expectedGeneration, long actualGeneration)
        => expectedGeneration is >= 0 and < long.MaxValue && actualGeneration == expectedGeneration + 1;

    private static bool HasDurableProof(GovernedLoopAdmissionResult result)
        => result.Status is GovernedLoopAdmissionStatus.Admitted or GovernedLoopAdmissionStatus.Replayed or GovernedLoopAdmissionStatus.Rejected
            && result.Outcome is not null
            && IsValidTerminalOutcome(result.Outcome);

    private static bool IsValidTerminalOutcome(GovernedLoopAdmissionTerminalOutcome outcome)
    {
        try
        {
            return GovernedLoopAdmissionValidator.Validate(outcome).IsValid;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsTrustedUtc(DateTimeOffset value) => value != default && value.Offset == TimeSpan.Zero;

    private static bool IsSha256(string? value)
        => value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsOciSha256(string? value)
        => value is { Length: 71 }
            && value.StartsWith("sha256:", StringComparison.Ordinal)
            && IsSha256(value[7..]);

    private static bool IsToken(string? value, int maximumLength)
        => !string.IsNullOrEmpty(value)
            && value.Length <= maximumLength
            && value[0] is >= 'a' and <= 'z' or >= '0' and <= '9'
            && value[^1] is >= 'a' and <= 'z' or >= '0' and <= '9'
            && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '.');

    private static GovernedLoopAdmissionResult Result(
        GovernedLoopAdmissionStatus status,
        GovernedLoopAdmissionRequest? request,
        GovernedLoopAdmissionTerminalOutcome? outcome = null)
        => new(status, request?.OperationId ?? string.Empty, request?.RequestHash ?? string.Empty, outcome);

    private static GovernedLoopGrantBindingResolution EmptyBinding(AuthorityGrantDependencyStatus status)
        => new(status, null, null, null, [], string.Empty);

    private static AuthorityGrantRoleResolution EmptyRole(AuthorityGrantDependencyStatus status)
        => new(status, null, null, null, string.Empty, ContextualRoleInstructionSourceProbeStatus.Unknown, string.Empty);

    private static AuthorityGrantResolution EmptyGrant(AuthorityGrantResolutionStatus status)
        => new(status, null, null, AuthorityCeilingIntersection.EmptyCeiling(), string.Empty, default);
}
