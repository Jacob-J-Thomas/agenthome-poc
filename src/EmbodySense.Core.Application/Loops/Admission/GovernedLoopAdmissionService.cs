using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Application.ContextualRoles.Models;
using EmbodySense.Core.Application.Governance.Authority.Grants;
using EmbodySense.Core.Application.Governance.Authority.Grants.Models;
using EmbodySense.Core.Application.Inference.Profiles;
using EmbodySense.Core.Application.Inference.Profiles.Models;
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
using EmbodySense.Core.Common.Inference.Profiles;
using EmbodySense.Core.Common.Inference.Profiles.Models;
using EmbodySense.Core.Common.Loops.Admission;
using EmbodySense.Core.Common.Loops.Admission.Models;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Custom.Graph;
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
    private readonly IGovernedModelRoutingAdmissionService _modelRoutingAdmissionService;
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
        IGovernedModelRoutingAdmissionService modelRoutingAdmissionService,
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
        _modelRoutingAdmissionService = modelRoutingAdmissionService ?? throw new ArgumentNullException(nameof(modelRoutingAdmissionService));
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

            if (read?.Status is not GovernedLoopAdmissionStoreReadStatus.Found and not GovernedLoopAdmissionStoreReadStatus.Recoverable)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            return readDisposition;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var storeGeneration = read!.StoreGeneration;
        var artifactRead = await ReadArtifactAsync(request, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
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
        cancellationToken.ThrowIfCancellationRequested();
        if (binding.Status != AuthorityGrantDependencyStatus.Active)
        {
            return MapBindingFailure(request, binding.Status);
        }

        if (!IsExactBinding(binding, request.Publication, artifact, intent.Role))
        {
            return Result(GovernedLoopAdmissionStatus.Ambiguous, request);
        }

        var role = await ResolveRoleAsync(intent.Role, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (role.Status != AuthorityGrantDependencyStatus.Active)
        {
            return await MapRoleFailureAsync(
                request,
                intent,
                artifact.RevisionArtifact.CreatedAtUtc,
                role,
                storeGeneration,
                cancellationToken).ConfigureAwait(false);
        }

        if (!IsExactActiveRole(role, intent.Role))
        {
            return Result(GovernedLoopAdmissionStatus.Ambiguous, request);
        }

        if (role.Revision!.Provenance.RecordedAtUtc > role.Lifecycle!.UpdatedAtUtc)
        {
            return Result(GovernedLoopAdmissionStatus.Ambiguous, request);
        }

        var grant = await ResolveGrantAsync(request.AuthorityGrant, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (grant.Status != AuthorityGrantResolutionStatus.Active)
        {
            return await MapGrantFailureAsync(
                request,
                intent,
                artifact.RevisionArtifact.CreatedAtUtc,
                role,
                grant,
                storeGeneration,
                cancellationToken).ConfigureAwait(false);
        }

        if (!IsExactActiveGrant(grant, request.AuthorityGrant))
        {
            return Result(GovernedLoopAdmissionStatus.Ambiguous, request);
        }

        if (artifact.RevisionArtifact.CreatedAtUtc > grant.EvaluatedAtUtc
            || role.Revision!.Provenance.RecordedAtUtc > role.Lifecycle!.UpdatedAtUtc
            || role.Lifecycle.UpdatedAtUtc > grant.EvaluatedAtUtc
            || grant.Grant!.RecordedAtUtc > grant.EvaluatedAtUtc
            || grant.Grant.Boundary.EffectiveAtUtc > grant.EvaluatedAtUtc
            || grant.Grant.Boundary.ExpiresAtUtc is { } evaluatedExpiry && evaluatedExpiry <= grant.EvaluatedAtUtc)
        {
            return Result(GovernedLoopAdmissionStatus.Ambiguous, request);
        }

        if (!Equals(grant.Grant!.Binding.Role, intent.Role))
        {
            return await CommitDefinitiveFailureAsync(
                request,
                intent,
                GovernedLoopAdmissionFailureCode.RoleMismatch,
                storeGeneration,
                grant.EvaluatedAtUtc,
                cancellationToken).ConfigureAwait(false);
        }

        if (!Equals(grant.Grant.Binding.Loop, request.Publication))
        {
            return await CommitDefinitiveFailureAsync(
                request,
                intent,
                GovernedLoopAdmissionFailureCode.GrantMismatch,
                storeGeneration,
                grant.EvaluatedAtUtc,
                cancellationToken).ConfigureAwait(false);
        }

        if (artifact.Graph.AuthorityCeiling.CapabilityIds.Count > CapabilityContractLimits.MaxDependencyManifestDependencies)
        {
            return Result(GovernedLoopAdmissionStatus.LimitExceeded, request);
        }

        if (!TryBuildCapabilityManifest(artifact, out var manifest, out var requirementsHash))
        {
            return Result(GovernedLoopAdmissionStatus.Ambiguous, request);
        }

        var graphIds = artifact.Graph.AuthorityCeiling.CapabilityIds.ToHashSet(StringComparer.Ordinal);
        var roleIds = role.Revision!.PolicyMaxima.CapabilityIds.ToHashSet(StringComparer.Ordinal);
        var policyCapabilities = grant.EffectiveCeiling.Capabilities
            .Where(item => roleIds.Contains(item.Id.Value))
            .ToArray();
        var policyAuthority = new AuthorityCeiling(
            policyCapabilities,
            grant.EffectiveCeiling.DataClasses,
            grant.EffectiveCeiling.MaxTargetCount,
            grant.EffectiveCeiling.MaxSideEffectClass,
            grant.EffectiveCeiling.AllowsRecurrence,
            grant.EffectiveCeiling.AllowsExternalPublication,
            grant.EffectiveCeiling.AllowsIrreversibleAction);
        if (!AuthorityProfileValidator.ValidateCeiling(policyAuthority).IsValid)
        {
            return Result(GovernedLoopAdmissionStatus.Ambiguous, request);
        }

        var policyIds = policyCapabilities.Select(item => item.Id.Value).ToHashSet(StringComparer.Ordinal);
        if (!graphIds.IsSubsetOf(policyIds))
        {
            return await CommitCapabilityFailureAsync(
                request,
                intent,
                manifest!,
                requirementsHash!,
                policyAuthority,
                grant,
                storeGeneration,
                cancellationToken).ConfigureAwait(false);
        }

        var allowedIds = new List<CapabilityId>(graphIds.Count);
        foreach (var id in graphIds.Order(StringComparer.Ordinal))
        {
            if (!CapabilityId.TryParse(id, out var parsed, out _))
            {
                return Result(GovernedLoopAdmissionStatus.Ambiguous, request);
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
            cancellationToken.ThrowIfCancellationRequested();
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
            || grant.EvaluatedAtUtc > capabilitySnapshot.AdmittedAtUtc
            || capabilitySnapshot.AdmittedAtUtc > evaluatedAtUtc)
        {
            return Result(GovernedLoopAdmissionStatus.Ambiguous, request);
        }

        if (grant.Grant!.Boundary.ExpiresAtUtc is { } expiry && expiry <= evaluatedAtUtc)
        {
            return Result(GovernedLoopAdmissionStatus.Ambiguous, request);
        }

        var exactPins = capabilitySnapshot.Pins.Select(item => item.DescriptorIdentity).ToHashSet();
        var effectiveCapabilities = grant.EffectiveCeiling.Capabilities
            .Where(item => roleIds.Contains(item.Id.Value) && graphIds.Contains(item.Id.Value) && exactPins.Contains(item))
            .ToArray();
        if (effectiveCapabilities.Select(item => item.Id.Value).ToHashSet(StringComparer.Ordinal).Count != graphIds.Count)
        {
            return Result(GovernedLoopAdmissionStatus.Ambiguous, request);
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
            cancellationToken.ThrowIfCancellationRequested();
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
            var routingNodes = ReachableInferenceNodes(artifact.Graph);
            var routingSeed = new GovernedModelRoutingAdmissionSeed(
                intent,
                executionBinding,
                grant.Grant.Binding.Profile,
                grant.Grant.Boundary,
                grant.DependencyEvidenceHash,
                effectiveAuthority,
                capabilitySnapshot,
                evaluatedAtUtc);
            var routingResult = await _modelRoutingAdmissionService.AdmitAsync(
                new GovernedModelRoutingAdmissionRequest(routingSeed, routingNodes),
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsValidRoutingResult(routingResult, routingSeed, routingNodes))
            {
                return Result(GovernedLoopAdmissionStatus.Ambiguous, request);
            }
            if (routingResult.Status == GovernedModelRoutingAdmissionStatus.Unavailable)
            {
                return Result(GovernedLoopAdmissionStatus.Unavailable, request);
            }
            if (routingResult.Status == GovernedModelRoutingAdmissionStatus.Ineligible)
            {
                return await CommitDefinitiveFailureAtAsync(
                    request,
                    intent,
                    GovernedLoopAdmissionFailureCode.ModelRoutingDenied,
                    authorityDenial: null,
                    capabilityDenial: null,
                    modelRoutingDenial: routingResult.DenialProof,
                    rejectedAtUtc: evaluatedAtUtc,
                    storeGeneration,
                    cancellationToken).ConfigureAwait(false);
            }
            var modelRoutingAdmission = routingResult.Snapshot!;
            var evidence = GovernedLoopAdmissionContractHash.Apply(new GovernedLoopAdmissionEvidence(
                GovernedLoopAdmissionEvidence.CurrentSchemaVersion,
                GovernedLoopAdmissionContractHash.ComputeIntentHash(intent),
                executionBinding,
                grant.Grant.Binding.Profile,
                grant.Grant.Boundary,
                grant.DependencyEvidenceHash,
                effectiveAuthority,
                capabilitySnapshot,
                modelRoutingAdmission,
                GovernedLoopAdmissionContractHash.CreateEvidenceReferences(intent, effectiveAuthority, capabilitySnapshot, modelRoutingAdmission),
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
        int remainingCommitAttempts = MaximumCommitAttempts,
        int remainingRecoveryFinalizations = 1)
    {
        if (remainingCommitAttempts <= 0)
        {
            return Result(GovernedLoopAdmissionStatus.Ambiguous, request);
        }

        var intentHash = GovernedLoopAdmissionContractHash.ComputeIntentHash(outcome.Intent);
        while (remainingCommitAttempts > 0)
        {
            if (honorCallerCancellation)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            remainingCommitAttempts--;
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
                return await RecoverUncertainCommitAsync(
                    request,
                    expectedGeneration,
                    remainingCommitAttempts,
                    allowSameRecoverableGeneration: committedStatus == GovernedLoopAdmissionStatus.Replayed,
                    remainingRecoveryFinalizations).ConfigureAwait(false);
            }

            if (commit is null || !Enum.IsDefined(commit.Status) || commit.Status == GovernedLoopAdmissionStoreCommitStatus.Unknown || commit.StoreGeneration < 0)
            {
                return await RecoverUncertainCommitAsync(
                    request,
                    expectedGeneration,
                    remainingCommitAttempts,
                    allowSameRecoverableGeneration: committedStatus == GovernedLoopAdmissionStatus.Replayed,
                    remainingRecoveryFinalizations).ConfigureAwait(false);
            }

            switch (commit.Status)
            {
                case GovernedLoopAdmissionStoreCommitStatus.Committed:
                    return IsDirectSuccessor(expectedGeneration, commit.StoreGeneration) && SameOutcome(commit.Outcome, outcome)
                        ? Result(committedStatus, request, outcome)
                        : Result(GovernedLoopAdmissionStatus.Ambiguous, request);
                case GovernedLoopAdmissionStoreCommitStatus.AlreadyCommitted:
                    if (commit.StoreGeneration <= expectedGeneration)
                    {
                        return Result(GovernedLoopAdmissionStatus.Ambiguous, request);
                    }

                    var committed = ClassifyCommittedOutcome(request, commit.Outcome);
                    return committed.Status == GovernedLoopAdmissionStatus.Replayed
                        ? committed
                        : Result(GovernedLoopAdmissionStatus.Ambiguous, request);
                case GovernedLoopAdmissionStoreCommitStatus.OperationConflict:
                    if (commit.StoreGeneration <= expectedGeneration)
                    {
                        return Result(GovernedLoopAdmissionStatus.Ambiguous, request);
                    }

                    if (commit.Outcome is null)
                    {
                        return Result(GovernedLoopAdmissionStatus.Ambiguous, request);
                    }

                    var conflict = ClassifyCommittedOutcome(request, commit.Outcome);
                    return conflict.Status == GovernedLoopAdmissionStatus.Conflict
                        ? conflict
                        : Result(GovernedLoopAdmissionStatus.Ambiguous, request);
                case GovernedLoopAdmissionStoreCommitStatus.GenerationConflict:
                    if (commit.Outcome is not null || commit.StoreGeneration <= expectedGeneration)
                    {
                        return Result(GovernedLoopAdmissionStatus.Ambiguous, request);
                    }

                    var reread = await ReadStoreAsync(request, CancellationToken.None).ConfigureAwait(false);
                    if (reread is null || reread.StoreGeneration < commit.StoreGeneration)
                    {
                        return Result(GovernedLoopAdmissionStatus.Ambiguous, request);
                    }

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
                            honorCallerCancellation = false;
                            continue;
                        }

                        return disposition;
                    }

                    expectedGeneration = reread!.StoreGeneration;
                    break;
                case GovernedLoopAdmissionStoreCommitStatus.LimitExceeded:
                    return commit.StoreGeneration == expectedGeneration && commit.Outcome is null
                        ? Result(GovernedLoopAdmissionStatus.LimitExceeded, request)
                        : Result(GovernedLoopAdmissionStatus.Ambiguous, request);
                case GovernedLoopAdmissionStoreCommitStatus.Unavailable:
                    return commit.StoreGeneration == expectedGeneration && commit.Outcome is null
                        ? Result(GovernedLoopAdmissionStatus.Unavailable, request)
                        : Result(GovernedLoopAdmissionStatus.Ambiguous, request);
                case GovernedLoopAdmissionStoreCommitStatus.Ambiguous:
                default:
                    return await RecoverUncertainCommitAsync(
                        request,
                        expectedGeneration,
                        remainingCommitAttempts,
                        allowSameRecoverableGeneration: committedStatus == GovernedLoopAdmissionStatus.Replayed,
                        remainingRecoveryFinalizations).ConfigureAwait(false);
            }
        }

        return Result(GovernedLoopAdmissionStatus.Ambiguous, request);
    }

    private async Task<GovernedLoopAdmissionResult> RecoverUncertainCommitAsync(
        GovernedLoopAdmissionRequest request,
        long minimumStoreGeneration,
        int remainingCommitAttempts,
        bool allowSameRecoverableGeneration,
        int remainingRecoveryFinalizations)
    {
        var read = await ReadStoreAsync(request, CancellationToken.None).ConfigureAwait(false);
        if (read is null
            || read.StoreGeneration < minimumStoreGeneration
            || read.StoreGeneration == minimumStoreGeneration
                && (read.Status != GovernedLoopAdmissionStoreReadStatus.Recoverable || !allowSameRecoverableGeneration))
        {
            return Result(GovernedLoopAdmissionStatus.Ambiguous, request);
        }

        var disposition = ClassifyRead(request, read);
        if (disposition is null)
        {
            return Result(GovernedLoopAdmissionStatus.Ambiguous, request);
        }

        if (read?.Status == GovernedLoopAdmissionStoreReadStatus.Recoverable
            && disposition.Status == GovernedLoopAdmissionStatus.Replayed
            && read.Outcome is not null
            && remainingCommitAttempts > 0
            && remainingRecoveryFinalizations > 0)
        {
            return await CommitOutcomeAsync(
                request,
                read.Outcome,
                read.StoreGeneration,
                GovernedLoopAdmissionStatus.Replayed,
                honorCallerCancellation: false,
                cancellationToken: CancellationToken.None,
                remainingCommitAttempts: remainingCommitAttempts,
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

    private async Task<GovernedLoopAdmissionResult> MapRoleFailureAsync(
        GovernedLoopAdmissionRequest request,
        GovernedLoopAdmissionIntent intent,
        DateTimeOffset artifactCreatedAtUtc,
        AuthorityGrantRoleResolution role,
        long storeGeneration,
        CancellationToken cancellationToken)
    {
        var code = (role.Status, role.SourceStatus) switch
        {
            (AuthorityGrantDependencyStatus.NotFound, ContextualRoleInstructionSourceProbeStatus.Missing)
                when HasExactMissingRoleSourceProof(role, intent.Role)
                => GovernedLoopAdmissionFailureCode.RoleSourceMismatch,
            (AuthorityGrantDependencyStatus.Disabled, ContextualRoleInstructionSourceProbeStatus.WorkspaceMismatch)
                when HasExactRoleWorkspaceMismatchProof(role, intent.Role)
                => GovernedLoopAdmissionFailureCode.RoleWorkspaceMismatch,
            (AuthorityGrantDependencyStatus.Disabled, ContextualRoleInstructionSourceProbeStatus.Ineligible)
                when HasExactInactiveRoleProof(role, intent.Role)
                => GovernedLoopAdmissionFailureCode.RoleInactive,
            (AuthorityGrantDependencyStatus.Stale, _) when HasExactReplacedRoleProof(role, intent.Role)
                => GovernedLoopAdmissionFailureCode.RoleReplaced,
            (AuthorityGrantDependencyStatus.NotFound, ContextualRoleInstructionSourceProbeStatus.Unknown)
                when HasExactAbsentRoleProof(role, intent.Role) => GovernedLoopAdmissionFailureCode.RoleNotFound,
            _ => GovernedLoopAdmissionFailureCode.None,
        };
        if (code != GovernedLoopAdmissionFailureCode.None)
        {
            if (!TryGetLatestEvidenceTime(
                    out var evidenceAtUtc,
                    artifactCreatedAtUtc,
                    role.Lifecycle?.UpdatedAtUtc ?? role.Revision?.Provenance.RecordedAtUtc))
            {
                return Result(GovernedLoopAdmissionStatus.Ambiguous, request);
            }

            return await CommitDefinitiveFailureAsync(
                request,
                intent,
                code,
                storeGeneration,
                evidenceAtUtc,
                cancellationToken).ConfigureAwait(false);
        }

        return role.Status == AuthorityGrantDependencyStatus.Unavailable
            ? Result(GovernedLoopAdmissionStatus.Unavailable, request)
            : Result(GovernedLoopAdmissionStatus.Ambiguous, request);
    }

    private async Task<GovernedLoopAdmissionResult> MapGrantFailureAsync(
        GovernedLoopAdmissionRequest request,
        GovernedLoopAdmissionIntent intent,
        DateTimeOffset artifactCreatedAtUtc,
        AuthorityGrantRoleResolution role,
        AuthorityGrantResolution resolution,
        long storeGeneration,
        CancellationToken cancellationToken)
    {
        var code = resolution.Status switch
        {
            AuthorityGrantResolutionStatus.NotEffective or AuthorityGrantResolutionStatus.Suspended or AuthorityGrantResolutionStatus.Revoked or AuthorityGrantResolutionStatus.Expired
                when HasExactInactiveGrantProof(resolution, request.AuthorityGrant) => GovernedLoopAdmissionFailureCode.GrantInactive,
            AuthorityGrantResolutionStatus.Stale or AuthorityGrantResolutionStatus.NotFound
                when HasExactGrantMismatchProof(resolution, request.AuthorityGrant) => GovernedLoopAdmissionFailureCode.GrantMismatch,
            _ => GovernedLoopAdmissionFailureCode.None,
        };
        if (code != GovernedLoopAdmissionFailureCode.None)
        {
            if (!TryGetLatestEvidenceTime(
                    out var evidenceAtUtc,
                    artifactCreatedAtUtc,
                    role.Revision!.Provenance.RecordedAtUtc,
                    role.Lifecycle!.UpdatedAtUtc,
                    resolution.Grant?.RecordedAtUtc,
                    resolution.EvaluatedAtUtc == default ? null : resolution.EvaluatedAtUtc))
            {
                return Result(GovernedLoopAdmissionStatus.Ambiguous, request);
            }

            return await CommitDefinitiveFailureAsync(
                request,
                intent,
                code,
                storeGeneration,
                evidenceAtUtc,
                cancellationToken,
                rejectionMustPrecedeUtc: resolution.Status == AuthorityGrantResolutionStatus.NotEffective
                    ? resolution.Grant!.Boundary.EffectiveAtUtc
                    : null).ConfigureAwait(false);
        }

        return resolution.Status is AuthorityGrantResolutionStatus.Unavailable
            or AuthorityGrantResolutionStatus.ProfileUnavailable
            or AuthorityGrantResolutionStatus.RoleUnavailable
            or AuthorityGrantResolutionStatus.LoopUnavailable
            ? Result(GovernedLoopAdmissionStatus.Unavailable, request)
            : Result(GovernedLoopAdmissionStatus.Ambiguous, request);
    }

    private async Task<GovernedLoopAdmissionResult> CommitCapabilityFailureAsync(
        GovernedLoopAdmissionRequest request,
        GovernedLoopAdmissionIntent intent,
        CapabilityDependencyManifest requirements,
        string requirementsHash,
        AuthorityCeiling effectiveAuthority,
        AuthorityGrantResolution grant,
        long storeGeneration,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetTrustedUtcNow(out var rejectedAtUtc))
        {
            return Result(GovernedLoopAdmissionStatus.Unavailable, request);
        }

        if (grant.EvaluatedAtUtc > rejectedAtUtc
            || grant.Grant?.Boundary.ExpiresAtUtc is { } expiry && expiry <= rejectedAtUtc)
        {
            return Result(GovernedLoopAdmissionStatus.Ambiguous, request);
        }

        var violations = requirements.Required
            .Where(dependency => !effectiveAuthority.Capabilities.Any(identity =>
                identity.Id.Equals(dependency.CapabilityId)
                && dependency.CompatibleVersionRange.Contains(identity.Version)))
            .OrderBy(dependency => dependency.CapabilityId.Value, StringComparer.Ordinal)
            .ThenBy(dependency => dependency.CompatibleVersionRange.Value, StringComparer.Ordinal)
            .Select(dependency => new GovernedLoopAdmissionCapabilityDenialViolation(
                dependency.CapabilityId,
                dependency.CompatibleVersionRange,
                GovernedLoopAdmissionCapabilityDenialReason.RequiredCapabilityOutsideEffectiveAuthority))
            .ToArray();
        if (violations.Length == 0)
        {
            return Result(GovernedLoopAdmissionStatus.Ambiguous, request);
        }

        var proof = new GovernedLoopAdmissionCapabilityDenialProof(
            GovernedLoopAdmissionCapabilityDenialProof.CurrentSchemaVersion,
            requirements,
            requirementsHash,
            effectiveAuthority,
            violations,
            rejectedAtUtc);
        return await CommitDefinitiveFailureAtAsync(
            request,
            intent,
            GovernedLoopAdmissionFailureCode.CapabilityResolutionDenied,
            authorityDenial: null,
            capabilityDenial: proof,
            modelRoutingDenial: null,
            rejectedAtUtc,
            storeGeneration,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<GovernedLoopAdmissionResult> CommitDefinitiveFailureAsync(
        GovernedLoopAdmissionRequest request,
        GovernedLoopAdmissionIntent intent,
        GovernedLoopAdmissionFailureCode failureCode,
        long storeGeneration,
        DateTimeOffset minimumEvidenceTimeUtc,
        CancellationToken cancellationToken,
        DateTimeOffset? rejectionMustPrecedeUtc = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetTrustedUtcNow(out var rejectedAtUtc))
        {
            return Result(GovernedLoopAdmissionStatus.Unavailable, request);
        }

        if (minimumEvidenceTimeUtc != default
            && (!IsTrustedUtc(minimumEvidenceTimeUtc) || minimumEvidenceTimeUtc > rejectedAtUtc))
        {
            return Result(GovernedLoopAdmissionStatus.Ambiguous, request);
        }

        if (rejectionMustPrecedeUtc is { } upperBoundUtc
            && (!IsTrustedUtc(upperBoundUtc) || rejectedAtUtc >= upperBoundUtc))
        {
            return Result(GovernedLoopAdmissionStatus.Ambiguous, request);
        }

        return await CommitDefinitiveFailureAtAsync(
            request,
            intent,
            failureCode,
            authorityDenial: null,
            capabilityDenial: null,
            modelRoutingDenial: null,
            rejectedAtUtc,
            storeGeneration,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<GovernedLoopAdmissionResult> CommitDefinitiveFailureAtAsync(
        GovernedLoopAdmissionRequest request,
        GovernedLoopAdmissionIntent intent,
        GovernedLoopAdmissionFailureCode failureCode,
        GovernedLoopAdmissionAuthorityDenialProof? authorityDenial,
        GovernedLoopAdmissionCapabilityDenialProof? capabilityDenial,
        GovernedLoopAdmissionModelRoutingDenialProof? modelRoutingDenial,
        DateTimeOffset rejectedAtUtc,
        long storeGeneration,
        CancellationToken cancellationToken)
    {
        GovernedLoopAdmissionTerminalOutcome outcome;
        try
        {
            var references = GovernedLoopAdmissionContractHash.CreateRejectionEvidenceReferences(
                intent,
                failureCode,
                authorityDenial,
                capabilityDenial,
                modelRoutingDenial);
            var rejection = GovernedLoopAdmissionContractHash.Apply(new GovernedLoopAdmissionRejection(
                GovernedLoopAdmissionRejection.CurrentSchemaVersion,
                intent,
                failureCode,
                authorityDenial,
                capabilityDenial,
                references,
                rejectedAtUtc,
                string.Empty,
                modelRoutingDenial));
            outcome = GovernedLoopAdmissionContractHash.Apply(new GovernedLoopAdmissionTerminalOutcome(
                GovernedLoopAdmissionTerminalOutcome.CurrentSchemaVersion,
                intent,
                GovernedLoopAdmissionDisposition.Rejected,
                null,
                rejection,
                rejectedAtUtc,
                string.Empty));
        }
        catch (ArgumentException)
        {
            return Result(GovernedLoopAdmissionStatus.Ambiguous, request);
        }

        if (!IsValidTerminalOutcome(outcome))
        {
            return Result(GovernedLoopAdmissionStatus.Ambiguous, request);
        }

        return await CommitOutcomeAsync(
            request,
            outcome,
            storeGeneration,
            GovernedLoopAdmissionStatus.Rejected,
            honorCallerCancellation: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private bool HasExactRoleFailureProof(AuthorityGrantRoleResolution role, ContextualRoleRevisionPin pin)
    {
        try
        {
            return Equals(role.RequestedPin, pin)
                && role.Revision is not null
                && ContextualRoleRevisionValidator.Validate(role.Revision).IsValid
                && Equals(role.Revision.Identity, pin.Identity)
                && string.Equals(role.Revision.ContentHash, pin.ContentHash, StringComparison.Ordinal)
                && role.Lifecycle is not null
                && role.Lifecycle.SchemaVersion == 1
                && string.Equals(role.Lifecycle.RoleId, pin.Identity.RoleId, StringComparison.Ordinal)
                && role.Lifecycle.CurrentIdentity is not null
                && string.Equals(role.Lifecycle.CurrentIdentity.RoleId, pin.Identity.RoleId, StringComparison.Ordinal)
                && role.Lifecycle.CurrentIdentity.Revision > 0
                && Enum.IsDefined(role.Lifecycle.State)
                && role.Lifecycle.State != ContextualRoleLifecycleState.Unknown
                && ContextualRoleId.IsValid(role.Lifecycle.LastOperationId)
                && Enum.IsDefined(role.Lifecycle.LastMutationKind)
                && role.Lifecycle.LastMutationKind != ContextualRoleRevisionMutationKind.Unknown
                && IsTrustedUtc(role.Lifecycle.UpdatedAtUtc)
                && role.Revision.Provenance.RecordedAtUtc <= role.Lifecycle.UpdatedAtUtc
                && string.Equals(role.WorkspaceId, _workspaceId, StringComparison.Ordinal)
                && IsSha256(role.EvidenceHash);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private bool HasExactMissingRoleSourceProof(AuthorityGrantRoleResolution role, ContextualRoleRevisionPin pin)
        => HasExactRoleFailureProof(role, pin)
            && role.Revision!.Status == ContextualRoleStatus.Published
            && role.Revision.WorkspaceApplicability.AppliesTo(_workspaceId)
            && role.Lifecycle!.State == ContextualRoleLifecycleState.Active
            && Equals(role.Lifecycle.CurrentIdentity, pin.Identity);

    private bool HasExactRoleWorkspaceMismatchProof(AuthorityGrantRoleResolution role, ContextualRoleRevisionPin pin)
        => HasExactRoleFailureProof(role, pin)
            && role.Revision!.Status == ContextualRoleStatus.Published
            && !role.Revision.WorkspaceApplicability.AppliesTo(_workspaceId)
            && role.Lifecycle!.State == ContextualRoleLifecycleState.Active
            && Equals(role.Lifecycle.CurrentIdentity, pin.Identity);

    private bool HasExactInactiveRoleProof(AuthorityGrantRoleResolution role, ContextualRoleRevisionPin pin)
        => HasExactRoleFailureProof(role, pin)
            && Equals(role.Lifecycle!.CurrentIdentity, pin.Identity)
            && (role.Revision!.Status != ContextualRoleStatus.Published
                || role.Lifecycle.State != ContextualRoleLifecycleState.Active);

    private bool HasExactReplacedRoleProof(AuthorityGrantRoleResolution role, ContextualRoleRevisionPin pin)
        => HasExactRoleFailureProof(role, pin)
            && !Equals(role.Lifecycle!.CurrentIdentity, pin.Identity);

    private bool HasExactAbsentRoleProof(AuthorityGrantRoleResolution role, ContextualRoleRevisionPin pin)
        => Equals(role.RequestedPin, pin)
            && role.Revision is null
            && role.Lifecycle is null
            && string.Equals(role.WorkspaceId, _workspaceId, StringComparison.Ordinal)
            && string.IsNullOrEmpty(role.EvidenceHash);

    private static bool HasExactInactiveGrantProof(AuthorityGrantResolution resolution, AuthorityGrantReference reference)
    {
        if (!HasExactGrantRecord(resolution, reference)
            || !HasCanonicalEmptyAuthority(resolution.EffectiveCeiling)
            || !string.IsNullOrEmpty(resolution.DependencyEvidenceHash)
            || !IsTrustedUtc(resolution.EvaluatedAtUtc)
            || resolution.Grant!.RecordedAtUtc > resolution.EvaluatedAtUtc)
        {
            return false;
        }

        var grant = resolution.Grant;
        return resolution.Status switch
        {
            AuthorityGrantResolutionStatus.NotEffective => grant.Status == AuthorityGrantLifecycleStatus.Active
                && resolution.EvaluatedAtUtc < grant.Boundary.EffectiveAtUtc,
            AuthorityGrantResolutionStatus.Suspended => grant.Status == AuthorityGrantLifecycleStatus.Suspended,
            AuthorityGrantResolutionStatus.Revoked => grant.Status == AuthorityGrantLifecycleStatus.Revoked,
            AuthorityGrantResolutionStatus.Expired => grant.Status == AuthorityGrantLifecycleStatus.Expired
                || grant.Boundary.ExpiresAtUtc is { } expiry && expiry <= resolution.EvaluatedAtUtc,
            _ => false,
        };
    }

    private static bool HasExactGrantMismatchProof(AuthorityGrantResolution resolution, AuthorityGrantReference reference)
    {
        if (!Equals(resolution.RequestedReference, reference)
            || !HasCanonicalEmptyAuthority(resolution.EffectiveCeiling)
            || !string.IsNullOrEmpty(resolution.DependencyEvidenceHash))
        {
            return false;
        }

        return resolution.Status switch
        {
            AuthorityGrantResolutionStatus.NotFound => resolution.Grant is null && resolution.EvaluatedAtUtc == default,
            AuthorityGrantResolutionStatus.Stale => resolution.EvaluatedAtUtc == default && HasExactGrantRecord(resolution, reference),
            _ => false,
        };
    }

    private static bool HasExactGrantRecord(AuthorityGrantResolution resolution, AuthorityGrantReference reference)
    {
        try
        {
            return Equals(resolution.RequestedReference, reference)
                && resolution.Grant is { } grant
                && AuthorityGrantHash.Matches(grant)
                && Equals(grant.GrantId, reference.GrantId)
                && Equals(grant.Revision, reference.Revision)
                && string.Equals(grant.ContentHash, reference.ContentHash, StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool HasCanonicalEmptyAuthority(AuthorityCeiling ceiling)
    {
        try
        {
            return AuthorityProfileValidator.ValidateCeiling(ceiling).IsValid
                && AuthorityCeilingSubset.IsEqual(ceiling, AuthorityCeilingIntersection.EmptyCeiling());
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TryGetLatestEvidenceTime(
        out DateTimeOffset latestTimeUtc,
        params DateTimeOffset?[] times)
    {
        latestTimeUtc = default;
        foreach (var time in times)
        {
            if (time is not { } value)
            {
                continue;
            }

            if (!IsTrustedUtc(value))
            {
                latestTimeUtc = default;
                return false;
            }

            if (value > latestTimeUtc)
            {
                latestTimeUtc = value;
            }
        }

        return latestTimeUtc != default;
    }

    private static bool IsValidRequest(GovernedLoopAdmissionRequest? request)
    {
        if (request is null
            || request.SchemaVersion != GovernedLoopAdmissionRequest.CurrentSchemaVersion
            || !IsToken(request.OperationId, GovernedLoopAdmissionLimits.MaxIdentifierCharacters)
            || !IsSha256(request.InvocationPayloadHash)
            || !IsSha256(request.RequestHash)
            || !IsToken(request.Surface, GovernedLoopAdmissionLimits.MaxSurfaceCharacters))
        {
            return false;
        }

        try
        {
            return GovernedLoopRevisionContractValidator.Validate(request.Publication).IsValid
                && request.AuthorityGrant?.GrantId is not null
                && AuthorityGrantId.TryParse(request.AuthorityGrant.GrantId.Value, out _, out _)
                && request.AuthorityGrant.Revision is not null
                && AuthorityGrantRevision.TryParse(
                    request.AuthorityGrant.Revision.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    out _,
                    out _)
                && IsOciSha256(request.AuthorityGrant.ContentHash)
                && request.ActorId is not null
                && AuthorityActorId.TryParse(request.ActorId.Value, out var actor, out _)
                && request.ActorId.Equals(actor)
                && GovernedLoopAdmissionRequestHash.Matches(request);
        }
        catch (Exception)
        {
            return false;
        }
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
        try
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
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsExactActiveGrant(
        AuthorityGrantResolution resolution,
        AuthorityGrantReference reference)
    {
        try
        {
            return Equals(resolution.RequestedReference, reference)
                && resolution.Grant is { Status: AuthorityGrantLifecycleStatus.Active } grant
                && AuthorityGrantHash.Matches(grant)
                && Equals(grant.GrantId, reference.GrantId)
                && Equals(grant.Revision, reference.Revision)
                && string.Equals(grant.ContentHash, reference.ContentHash, StringComparison.Ordinal)
                && AuthorityProfileValidator.ValidateCeiling(resolution.EffectiveCeiling).IsValid
                && AuthorityCeilingSubset.IsEqual(resolution.EffectiveCeiling, grant.RequestedCeiling)
                && IsSha256(resolution.DependencyEvidenceHash)
                && IsTrustedUtc(resolution.EvaluatedAtUtc);
        }
        catch (Exception)
        {
            return false;
        }
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

    private static IReadOnlyList<GovernedModelRoutingNodeAdmissionRequest> ReachableInferenceNodes(GovernedLoopGraphDefinition graph)
    {
        var nodes = graph.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var outgoing = graph.ControlEdges
            .GroupBy(edge => edge.FromNodeId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(edge => edge.ToNodeId).Distinct(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        var reachable = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>();
        pending.Enqueue(graph.EntryNodeId);
        while (pending.Count > 0)
        {
            var nodeId = pending.Dequeue();
            if (!reachable.Add(nodeId) || !outgoing.TryGetValue(nodeId, out var targets))
            {
                continue;
            }
            foreach (var target in targets)
            {
                pending.Enqueue(target);
            }
        }

        return Array.AsReadOnly(reachable
            .Select(nodeId => nodes[nodeId])
            .Where(node => node.Descriptor.Kind == GovernedLoopNodeKind.Inference)
            .OrderBy(node => node.Id, StringComparer.Ordinal)
            .Select(node => new GovernedModelRoutingNodeAdmissionRequest(
                node.Id,
                node.Descriptor.TypeId,
                node.ModelRoutingPolicy ?? graph.DefaultModelRoutingPolicy,
                node.AuthoredInputDataClasses))
            .ToArray());
    }

    private static bool IsValidRoutingResult(
        GovernedModelRoutingAdmissionResult? result,
        GovernedModelRoutingAdmissionSeed seed,
        IReadOnlyList<GovernedModelRoutingNodeAdmissionRequest> requestedNodes)
    {
        try
        {
            if (result is null || !Enum.IsDefined(result.Status))
            {
                return false;
            }

            if (result.Status == GovernedModelRoutingAdmissionStatus.Unavailable)
            {
                return result.Snapshot is null && result.DenialProof is null;
            }

            if (result.Status == GovernedModelRoutingAdmissionStatus.Ineligible)
            {
                var proof = result.DenialProof;
                return result.Snapshot is null
                    && GovernedLoopAdmissionValidator.Validate(proof).IsValid
                    && proof!.EvaluatedAtUtc == seed.EvaluatedAtUtc
                    && string.Equals(proof.EffectiveAuthorityReferenceHash, GovernedLoopAdmissionContractHash.ComputeAuthorityCeilingReferenceHash(seed.EffectiveAuthority), StringComparison.Ordinal)
                    && string.Equals(proof.CapabilityAdmissionReferenceHash, GovernedLoopAdmissionContractHash.ComputeCapabilityAdmissionReferenceHash(seed.CapabilityAdmission), StringComparison.Ordinal)
                    && requestedNodes.Any(node => string.Equals(node.NodeId, proof.NodeId, StringComparison.Ordinal)
                        && string.Equals(node.NodeTypeId, proof.NodeTypeId, StringComparison.Ordinal)
                        && string.Equals(node.Policy.ContentHash, proof.PolicyHash, StringComparison.Ordinal)
                        && IsExactRoutingDenial(node.Policy, proof));
            }

            if (result.Status != GovernedModelRoutingAdmissionStatus.Admitted
                || result.DenialProof is not null
                || !GovernedModelContractValidator.IsValid(result.Snapshot))
            {
                return false;
            }

            var snapshot = result.Snapshot!;
            var admittedPins = seed.CapabilityAdmission.Pins.ToDictionary(pin => pin.DescriptorIdentity.Id.Value, StringComparer.Ordinal);
            var expectedEntries = requestedNodes.Select(node =>
            {
                var candidateIds = node.Policy.ResolveCandidateOrder(snapshot.ResolvedDefaultProfileId);
                return (Node: node, CandidateIds: candidateIds);
            }).ToArray();
            if (expectedEntries.Any(item => item.CandidateIds.Count == 0)
                || snapshot.Entries.Count != expectedEntries.Length)
            {
                return false;
            }

            var exactEntryBindings = snapshot.Entries.Zip(expectedEntries).All(pair =>
            {
                var entry = pair.First;
                var requested = pair.Second.Node;
                var expectedIds = pair.Second.CandidateIds;
                var pins = new[] { entry.Primary }.Concat(entry.Fallbacks).ToArray();
                return string.Equals(entry.NodeId, requested.NodeId, StringComparison.Ordinal)
                    && string.Equals(entry.NodeTypeId, requested.NodeTypeId, StringComparison.Ordinal)
                    && string.Equals(entry.PolicyHash, requested.Policy.ContentHash, StringComparison.Ordinal)
                    && string.Equals(entry.Requirements.ContentHash, requested.Policy.Requirements.ContentHash, StringComparison.Ordinal)
                    && entry.HasAuthoredInputClassification == (requested.AuthoredInputDataClasses is not null)
                    && entry.AuthoredInputDataClasses.SequenceEqual(requested.AuthoredInputDataClasses ?? Array.Empty<CapabilityDataClass>())
                    && pins.Select(pin => pin.Capability.DescriptorIdentity.Id).SequenceEqual(expectedIds)
                    && pins.All(pin => admittedPins.TryGetValue(pin.Capability.DescriptorIdentity.Id.Value, out var admittedPin)
                        && string.Equals(CapabilityAdmissionPinHash.Compute(pin.Capability), CapabilityAdmissionPinHash.Compute(admittedPin), StringComparison.Ordinal)
                        && string.Equals(pin.AdapterRegistryRevisionHash, snapshot.AdapterRegistryRevisionHash, StringComparison.Ordinal)
                        && requested.Policy.Requirements.StaticallySatisfiedBy(pin.Metadata, seed.Intent.Role.Identity.RoleId, requested.NodeTypeId)
                        && (requested.AuthoredInputDataClasses is null
                            || requested.Policy.Requirements.SatisfiedBy(pin.Metadata, requested.AuthoredInputDataClasses, seed.Intent.Role.Identity.RoleId, requested.NodeTypeId)));
            });

            return exactEntryBindings
                && string.Equals(snapshot.WorkspaceId, seed.Intent.WorkspaceId, StringComparison.Ordinal)
                && string.Equals(snapshot.AdmissionOperationId, seed.Intent.OperationId, StringComparison.Ordinal)
                && string.Equals(snapshot.AdmissionIntentHash, GovernedLoopAdmissionContractHash.ComputeIntentHash(seed.Intent), StringComparison.Ordinal)
                && string.Equals(snapshot.ExecutionBindingReferenceHash, GovernedLoopAdmissionContractHash.ComputeExecutionBindingReferenceHash(seed.Binding), StringComparison.Ordinal)
                && string.Equals(snapshot.RunId, seed.Binding.RunId, StringComparison.Ordinal)
                && string.Equals(snapshot.GraphId, seed.Binding.Revision.GraphId, StringComparison.Ordinal)
                && string.Equals(snapshot.GraphRevisionId, seed.Binding.Revision.RevisionId, StringComparison.Ordinal)
                && string.Equals(snapshot.GraphExecutableHash, seed.Binding.Revision.ExecutableHash, StringComparison.Ordinal)
                && snapshot.ExecutionGeneration == seed.Binding.ExecutionGeneration
                && string.Equals(snapshot.OwningRoleId, seed.Intent.Role.Identity.RoleId, StringComparison.Ordinal)
                && snapshot.OwningRoleRevision == seed.Intent.Role.Identity.Revision
                && string.Equals(snapshot.OwningRoleContentHash, seed.Intent.Role.ContentHash, StringComparison.Ordinal)
                && string.Equals(snapshot.CapabilityAdmissionReferenceHash, GovernedLoopAdmissionContractHash.ComputeCapabilityAdmissionReferenceHash(seed.CapabilityAdmission), StringComparison.Ordinal)
                && string.Equals(snapshot.AuthorityAdmissionReferenceHash, GovernedLoopAdmissionContractHash.ComputeAdmissionAuthorityReferenceHash(seed.GrantProfile, seed.GrantBoundary, seed.GrantDependencyEvidenceHash, seed.EffectiveAuthority), StringComparison.Ordinal)
                && snapshot.EvaluatedAtUtc == seed.EvaluatedAtUtc
                && (snapshot.ResolvedDefaultProfileId is null) == requestedNodes.All(node => node.Policy.Selector.Kind != GovernedModelSelectorKind.Inherit)
                && (snapshot.DefaultSourceRevisionHash is null) == requestedNodes.All(node => node.Policy.Selector.Kind != GovernedModelSelectorKind.Inherit)
                && (snapshot.AdapterRegistryRevisionHash is null) == (requestedNodes.Count == 0);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsExactRoutingDenial(
        GovernedModelRoutingPolicy policy,
        GovernedLoopAdmissionModelRoutingDenialProof proof)
    {
        if (proof.Reason == GovernedLoopAdmissionModelRoutingDenialReason.DefaultNotConfigured)
        {
            return proof.CandidateProfileId is null
                && policy.Selector.Kind == GovernedModelSelectorKind.Inherit;
        }

        if (proof.CandidateProfileId is null)
        {
            return false;
        }

        var permittedCandidates = policy.Selector.Kind switch
        {
            GovernedModelSelectorKind.Exact => new[] { policy.Selector.ExactProfileId! }.Concat(policy.FallbackProfileIds),
            GovernedModelSelectorKind.Inherit => policy.Selector.PermittedInheritedProfileIds.Concat(policy.FallbackProfileIds),
            _ => Array.Empty<CapabilityId>()
        };
        return permittedCandidates.Contains(proof.CandidateProfileId);
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
        => new(
            status,
            IsToken(request?.OperationId, GovernedLoopAdmissionLimits.MaxIdentifierCharacters) ? request!.OperationId : string.Empty,
            status != GovernedLoopAdmissionStatus.Invalid && IsSha256(request?.RequestHash) ? request!.RequestHash : string.Empty,
            outcome);

    private static GovernedLoopGrantBindingResolution EmptyBinding(AuthorityGrantDependencyStatus status)
        => new(status, null, null, null, [], string.Empty);

    private static AuthorityGrantRoleResolution EmptyRole(AuthorityGrantDependencyStatus status)
        => new(status, null, null, null, string.Empty, ContextualRoleInstructionSourceProbeStatus.Unknown, string.Empty);

    private static AuthorityGrantResolution EmptyGrant(AuthorityGrantResolutionStatus status)
        => new(status, null, null, AuthorityCeilingIntersection.EmptyCeiling(), string.Empty, default);
}
