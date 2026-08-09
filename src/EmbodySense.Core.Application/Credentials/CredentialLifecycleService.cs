using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Application.Credentials.Models;
using EmbodySense.Core.Application.Governance.Audit;
using EmbodySense.Core.Common.Credentials;
using EmbodySense.Core.Common.Credentials.Models;
using EmbodySense.Core.Common.Governance.Audit;

namespace EmbodySense.Core.Application.Credentials;

/// <summary>Orchestrates actor-bound, preview-bound, audited credential lifecycle transitions without exposing values or locators.</summary>
/// <remarks>Registration and consent remain metadata and never grant loop, capability, or runtime authority. Value-bearing provider mutations are preceded by durable repair-required registry intent so restart ambiguity cannot trigger an automatic retry.</remarks>
public sealed class CredentialLifecycleService
{
    private const int MaximumActiveRuns = 1_024;
    private readonly ICredentialRegistryStore _registry;
    private readonly ICredentialValueProvider _provider;
    private readonly ICredentialProviderLocatorSource _locatorSource;
    private readonly ICapabilityDependentIndex _dependentIndex;
    private readonly ICredentialActiveRunIndex _activeRunIndex;
    private readonly IAuditLog _auditLog;
    private readonly ICapabilityAuthorityTransaction _authorityTransaction;

    /// <summary>Creates a lifecycle orchestrator over already-governed application ports.</summary>
    /// <param name="registry">The closed registry port that authenticates actors and commits lifecycle evidence.</param>
    /// <param name="provider">The value provider used only during value-bearing lifecycle operations.</param>
    /// <param name="locatorSource">The provider-owned source of opaque registration locators.</param>
    /// <param name="dependentIndex">The complete capability dependent index used for destructive previews.</param>
    /// <param name="activeRunIndex">The authoritative active-run index used for restrictive transitions.</param>
    /// <param name="auditLog">The append-only audit sink used for lifecycle observations.</param>
    /// <param name="authorityTransaction">The transaction that serializes lifecycle observations and mutations.</param>
    /// <remarks>Composition supplies ports that preserve registry authentication and lifecycle mutation authority. This constructor does not expose a raw persistence mutation API or grant authority to its callers.</remarks>
    public CredentialLifecycleService(ICredentialRegistryStore registry, ICredentialValueProvider provider, ICredentialProviderLocatorSource locatorSource, ICapabilityDependentIndex dependentIndex, ICredentialActiveRunIndex activeRunIndex, IAuditLog auditLog, ICapabilityAuthorityTransaction authorityTransaction)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _locatorSource = locatorSource ?? throw new ArgumentNullException(nameof(locatorSource));
        _dependentIndex = dependentIndex ?? throw new ArgumentNullException(nameof(dependentIndex));
        _activeRunIndex = activeRunIndex ?? throw new ArgumentNullException(nameof(activeRunIndex));
        _auditLog = auditLog ?? throw new ArgumentNullException(nameof(auditLog));
        _authorityTransaction = authorityTransaction ?? throw new ArgumentNullException(nameof(authorityTransaction));
    }

    /// <summary>Captures an exact destructive-operation impact preview under the shared capability-authority transaction.</summary>
    public async Task<CredentialLifecyclePreview> PreviewAsync(CredentialLifecyclePreviewRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var target = request.ReferenceId?.Value ?? "invalid";
        if (!ValidatePreviewRequest(request))
        {
            return await AuditPreviewAsync(new CredentialLifecyclePreview(CredentialLifecyclePreviewStatus.Invalid, request.OperationId, request.Kind, request.ReferenceId!, request.WorkspaceId, request.ActorId, null, string.Empty, string.Empty, [], "The credential lifecycle preview request is invalid."), target);
        }

        var authentication = await _registry.AuthenticateActorAsync(request.ActorId, cancellationToken);
        if (authentication != CredentialActorAuthentication.AuthenticatedUser)
        {
            return await AuditPreviewAsync(new CredentialLifecyclePreview(CredentialLifecyclePreviewStatus.Denied, request.OperationId, request.Kind, request.ReferenceId!, request.WorkspaceId, request.ActorId, null, string.Empty, string.Empty, [], "An authenticated user is required for a destructive credential impact preview."), target);
        }
        var preview = await _authorityTransaction.ExecuteAsync(async transactionCancellationToken =>
        {
            var read = await _registry.ReadAsync(transactionCancellationToken);
            if (!read.Succeeded)
            {
                return new CredentialLifecyclePreview(CredentialLifecyclePreviewStatus.Unavailable, request.OperationId, request.Kind, request.ReferenceId!, request.WorkspaceId, request.ActorId, null, string.Empty, string.Empty, [], "The credential registry is unavailable.");
            }
            if (read.RegistryRevision != request.ExpectedRegistryRevision)
            {
                return new CredentialLifecyclePreview(CredentialLifecyclePreviewStatus.Conflict, request.OperationId, request.Kind, request.ReferenceId!, request.WorkspaceId, request.ActorId, read.RegistryRevision, string.Empty, string.Empty, [], "The expected credential registry revision is stale.");
            }
            var entry = read.Entries.SingleOrDefault(candidate => candidate.Reference.Id.Equals(request.ReferenceId));
            var tombstone = read.Tombstones.SingleOrDefault(candidate => candidate.ReferenceId.Equals(request.ReferenceId));
            var preparedCreateRepair = IsPreparedCreateRepairCandidate(read, request.ReferenceId!);
            var interruptedRepair = FindUnresolvedRepairIntent(read, request.ReferenceId!, request.InterruptedRepairOperationId);
            CredentialCapabilityBinding binding;
            CredentialContractHash bindingHash;
            long targetRevision;
            if (request.Kind is CredentialLifecycleOperationKind.Repair or CredentialLifecycleOperationKind.ReconcileRepair)
            {
                if (request.Kind == CredentialLifecycleOperationKind.Repair && HasUnresolvedRepairIntent(read, request.ReferenceId!))
                {
                    return new CredentialLifecyclePreview(CredentialLifecyclePreviewStatus.Conflict, request.OperationId, request.Kind, request.ReferenceId!, request.WorkspaceId, request.ActorId, read.RegistryRevision, string.Empty, string.Empty, [], "An interrupted repair intent must be explicitly reconciled before another repair preview.");
                }
                if (request.Kind == CredentialLifecycleOperationKind.ReconcileRepair && interruptedRepair is null)
                {
                    return new CredentialLifecyclePreview(CredentialLifecyclePreviewStatus.Conflict, request.OperationId, request.Kind, request.ReferenceId!, request.WorkspaceId, request.ActorId, read.RegistryRevision, string.Empty, string.Empty, [], "The exact interrupted repair intent is absent, terminal, or belongs to another credential.");
                }
                if (preparedCreateRepair && entry is not null)
                {
                    binding = entry.Binding;
                    bindingHash = entry.BindingHash;
                    targetRevision = entry.Revision;
                }
                else if (entry is null && tombstone is { NeedsRepair: true, RepairBinding: not null, RepairProviderId: not null })
                {
                    binding = tombstone.RepairBinding;
                    if (!CredentialContractJson.TryHash(binding, out var repairBindingHash, out _))
                    {
                        return new CredentialLifecyclePreview(CredentialLifecyclePreviewStatus.Unavailable, request.OperationId, request.Kind, request.ReferenceId!, request.WorkspaceId, request.ActorId, read.RegistryRevision, string.Empty, string.Empty, [], "The repair-required binding is invalid.");
                    }
                    bindingHash = repairBindingHash!;
                    targetRevision = tombstone.Revision;
                }
                else
                {
                    return new CredentialLifecyclePreview(CredentialLifecyclePreviewStatus.NotFound, request.OperationId, request.Kind, request.ReferenceId!, request.WorkspaceId, request.ActorId, read.RegistryRevision, string.Empty, string.Empty, [], "A repair-required prepared registration or credential tombstone was not found.");
                }
            }
            else
            {
                if (entry is null)
                {
                    return new CredentialLifecyclePreview(CredentialLifecyclePreviewStatus.NotFound, request.OperationId, request.Kind, request.ReferenceId!, request.WorkspaceId, request.ActorId, read.RegistryRevision, string.Empty, string.Empty, [], "The credential reference was not found.");
                }
                binding = entry.Binding;
                bindingHash = entry.BindingHash;
                targetRevision = entry.Revision;
            }
            if (!string.Equals(binding.Scope.WorkspaceId, request.WorkspaceId, StringComparison.Ordinal))
            {
                return new CredentialLifecyclePreview(CredentialLifecyclePreviewStatus.Conflict, request.OperationId, request.Kind, request.ReferenceId!, request.WorkspaceId, request.ActorId, read.RegistryRevision, string.Empty, string.Empty, [], "The preview workspace does not match the credential binding.");
            }

            var first = await _dependentIndex.CaptureAsync(transactionCancellationToken);
            var second = first.Status == CapabilityDependentIndexStatus.Available ? await _dependentIndex.CaptureAsync(transactionCancellationToken) : first;
            if (first.Status != CapabilityDependentIndexStatus.Available || second.Status != CapabilityDependentIndexStatus.Available || !string.Equals(first.Hash, second.Hash, StringComparison.Ordinal))
            {
                return new CredentialLifecyclePreview(CredentialLifecyclePreviewStatus.Unavailable, request.OperationId, request.Kind, request.ReferenceId!, request.WorkspaceId, request.ActorId, read.RegistryRevision, string.Empty, string.Empty, [], "The complete registered dependent set is unavailable or changed during capture.");
            }

            var impacts = ProjectImpacts(binding, second.Dependents);
            var revision = ComputePreviewRevision(request, bindingHash, targetRevision, second.Hash, impacts);
            return new CredentialLifecyclePreview(CredentialLifecyclePreviewStatus.Ready, request.OperationId, request.Kind, request.ReferenceId!, request.WorkspaceId, request.ActorId, read.RegistryRevision, second.Hash, revision, impacts, "The exact credential impact preview is ready for explicit confirmation.");
        }, cancellationToken);
        return await AuditPreviewAsync(preview, target);
    }

    /// <summary>Executes one lifecycle transition. Value-bearing operations require a callback source and never retain it.</summary>
    public async Task<CredentialLifecycleResult> ExecuteAsync(CredentialLifecycleRequest request, CredentialSecretWriteCallback? source = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var outcome = await _authorityTransaction.ExecuteAsync(transactionCancellationToken => ExecuteUnderAuthorityAsync(request, source, transactionCancellationToken), cancellationToken);
        if (request.OperationId is not null && request.ReferenceId is not null && IsSafe(request.ActorId, 128))
        {
            var read = await _registry.ReadAsync(CancellationToken.None);
            var hasDurableOutcome = read.Succeeded && read.Operations.Any(operation => (operation.LifecycleIntentOperationId?.Equals(request.OperationId) == true || operation.OperationId.Equals(request.OperationId)) && IsTerminalPhase(operation.LifecyclePhase) && MatchesLifecycleEvidence(operation, request));
            if (hasDurableOutcome)
            {
                _ = await DrainAuditAsync(CancellationToken.None);
            }
            else
            {
                await TryAppendAuditAsync(AuditSchema.Actions.CredentialLifecycleOutcome, request, AuditOutcome(outcome.Status), outcome.RegistryRevision, request.Preview?.PreviewRevision, outcome.Detail);
            }
        }
        return outcome;
    }

    /// <summary>Reconciles durable credential lifecycle events to the audit sink with at-least-once delivery.</summary>
    /// <remarks>A crash after sink append but before registry acknowledgement can produce a duplicate event. Stable audit-operation correlation metadata lets consumers deduplicate without risking permanent loss.</remarks>
    /// <param name="cancellationToken">Stops before beginning another delivery attempt; completed sink appends are still acknowledged durably.</param>
    /// <returns>The number of acknowledged and still-pending lifecycle audit items, plus any retryable drain failure.</returns>
    public Task<CredentialLifecycleAuditDrainResult> DrainAuditAsync(CancellationToken cancellationToken = default) => _authorityTransaction.ExecuteAsync(DrainAuditUnderAuthorityAsync, cancellationToken);

    private async Task<CredentialLifecycleResult> ExecuteUnderAuthorityAsync(CredentialLifecycleRequest request, CredentialSecretWriteCallback? source, CancellationToken cancellationToken)
    {
        var invalid = ValidateRequest(request, source);
        if (invalid is not null)
        {
            return invalid;
        }

        var authentication = await _registry.AuthenticateActorAsync(request.ActorId, cancellationToken);
        if (authentication == CredentialActorAuthentication.Unauthenticated || RequiresUser(request.Kind) && authentication != CredentialActorAuthentication.AuthenticatedUser)
        {
            return Result(request, CredentialLifecycleResultStatus.Denied, null, CredentialProviderHealthStatus.Unavailable, [], CredentialFailureCode.Unauthorized, "The lifecycle actor lacks the required authenticated user posture.");
        }
        var read = await _registry.ReadAsync(cancellationToken);
        if (!read.Succeeded)
        {
            return Result(request, CredentialLifecycleResultStatus.Unavailable, null, CredentialProviderHealthStatus.Unavailable, [], CredentialFailureCode.Unavailable, "The credential registry is unavailable.");
        }

        var existingOperation = read.Operations.SingleOrDefault(operation => operation.OperationId.Equals(request.OperationId));
        var entry = read.Entries.SingleOrDefault(candidate => candidate.Reference.Id.Equals(request.ReferenceId));
        var tombstone = read.Tombstones.SingleOrDefault(candidate => candidate.ReferenceId.Equals(request.ReferenceId));
        var preparedCreateRepair = IsPreparedCreateRepairCandidate(read, request.ReferenceId);
        var interruptedRepair = FindUnresolvedRepairIntent(read, request.ReferenceId, request.InterruptedRepairOperationId);
        var workspaceFailure = ValidateWorkspaceBinding(request, entry, tombstone);
        if (workspaceFailure is not null)
        {
            return workspaceFailure;
        }
        if (existingOperation is not null)
        {
            if (!MatchesLifecycleEvidence(existingOperation, request))
            {
                return Result(request, CredentialLifecycleResultStatus.Conflict, read.RegistryRevision, entry?.Health ?? CredentialProviderHealthStatus.Missing, [], CredentialFailureCode.Conflict, "The operation identity was reused with changed lifecycle intent.");
            }
            if (HasProviderMutation(request.Kind))
            {
                return ReplayProviderOperation(request, read, entry);
            }
            if (request.Kind == CredentialLifecycleOperationKind.Test && existingOperation.LifecyclePhase == CredentialLifecycleMutationPhase.MetadataComplete && existingOperation.ResultHealth is CredentialProviderHealthStatus.Unavailable or CredentialProviderHealthStatus.Corrupt)
            {
                var status = existingOperation.ResultHealth == CredentialProviderHealthStatus.Corrupt ? CredentialLifecycleResultStatus.Failed : CredentialLifecycleResultStatus.Unavailable;
                return Result(request, status, read.RegistryRevision, existingOperation.ResultHealth.Value, [], CredentialFailureCode.Unavailable, "The exact failed provider health result was replayed from durable terminal evidence without repeating provider I/O.");
            }
            return Result(request, CredentialLifecycleResultStatus.Replayed, read.RegistryRevision, entry?.Health ?? CredentialProviderHealthStatus.Missing, existingOperation.AffectedActiveRuns, null, "The exact value-free lifecycle operation was already committed.");
        }
        if (HasUnresolvedRepairIntent(read, request.ReferenceId) && !(request.Kind == CredentialLifecycleOperationKind.ReconcileRepair && interruptedRepair is not null))
        {
            return Result(request, CredentialLifecycleResultStatus.Conflict, read.RegistryRevision, CredentialProviderHealthStatus.NeedsRepair, [], CredentialFailureCode.Conflict, "An interrupted repair intent must be reconciled before another lifecycle operation.");
        }
        if (HasUnresolvedCreateIntent(read, request.ReferenceId) && request.Kind is not (CredentialLifecycleOperationKind.Repair or CredentialLifecycleOperationKind.ReconcileRepair))
        {
            return Result(request, CredentialLifecycleResultStatus.Conflict, read.RegistryRevision, CredentialProviderHealthStatus.NeedsRepair, [], CredentialFailureCode.Conflict, "An unresolved create/import locator or provider intent must be repaired before another lifecycle operation.");
        }
        if (entry?.Health == CredentialProviderHealthStatus.NeedsRepair && !(request.Kind == CredentialLifecycleOperationKind.Repair && preparedCreateRepair || request.Kind == CredentialLifecycleOperationKind.ReconcileRepair && interruptedRepair is not null))
        {
            return Result(request, CredentialLifecycleResultStatus.Conflict, read.RegistryRevision, CredentialProviderHealthStatus.NeedsRepair, [], CredentialFailureCode.Conflict, "An unresolved provider-mutation intent must be repaired or explicitly resolved before another lifecycle operation.");
        }
        if (read.RegistryRevision != request.ExpectedRegistryRevision)
        {
            return Result(request, CredentialLifecycleResultStatus.Conflict, read.RegistryRevision, entry?.Health ?? CredentialProviderHealthStatus.Missing, [], CredentialFailureCode.Conflict, "The expected credential registry revision is stale.");
        }

        var transitionFailure = ValidateTransition(request, entry, tombstone, preparedCreateRepair, interruptedRepair);
        if (transitionFailure is not null)
        {
            return transitionFailure;
        }
        var previewBinding = entry?.Binding ?? tombstone?.RepairBinding;
        var previewBindingHash = entry?.BindingHash;
        if (previewBindingHash is null && previewBinding is not null)
        {
            _ = CredentialContractJson.TryHash(previewBinding, out previewBindingHash, out _);
        }
        var previewTargetRevision = entry?.Revision ?? tombstone?.Revision;
        if (RequiresPreview(request.Kind) && (previewBinding is null || previewBindingHash is null || previewTargetRevision is null || !await ValidateConfirmedPreviewAsync(request, previewBinding, previewBindingHash, previewTargetRevision.Value, cancellationToken)))
        {
            var health = entry?.Health ?? (tombstone?.NeedsRepair == true ? CredentialProviderHealthStatus.NeedsRepair : CredentialProviderHealthStatus.Missing);
            return Result(request, CredentialLifecycleResultStatus.Conflict, read.RegistryRevision, health, [], CredentialFailureCode.Conflict, "The confirmed impact preview is stale, incomplete, or belongs to another actor, workspace, or request.");
        }
        CredentialLifecycleResult outcome;
        if (request.Kind is CredentialLifecycleOperationKind.Create or CredentialLifecycleOperationKind.Import)
        {
            outcome = await CreateAsync(request, source!, cancellationToken);
        }
        else if (request.Kind is CredentialLifecycleOperationKind.Rotate or CredentialLifecycleOperationKind.Replace)
        {
            outcome = await ReplaceAsync(request, entry!, source!, cancellationToken);
        }
        else if (request.Kind == CredentialLifecycleOperationKind.Delete)
        {
            outcome = await DeleteAsync(request, entry!, cancellationToken);
        }
        else if (request.Kind == CredentialLifecycleOperationKind.Repair)
        {
            outcome = await RepairAsync(request, entry, tombstone, cancellationToken);
        }
        else if (request.Kind == CredentialLifecycleOperationKind.ReconcileRepair)
        {
            outcome = await ReconcileRepairAsync(request, read.RegistryRevision!.Value, cancellationToken);
        }
        else if (request.Kind == CredentialLifecycleOperationKind.Test)
        {
            outcome = await TestAsync(request, entry!, cancellationToken);
        }
        else
        {
            outcome = await ApplyMetadataAsync(request, entry!, cancellationToken);
        }

        return outcome;
    }

    private async Task<CredentialLifecycleResult> CreateAsync(CredentialLifecycleRequest request, CredentialSecretWriteCallback source, CancellationToken cancellationToken)
    {
        var intent = Mutation(request, CredentialRegistryMutationKind.BeginCreate, request.ExpectedRegistryRevision, request.Reference, request.Binding, request.ConsentReference, CredentialProviderHealthStatus.NeedsRepair, null, false, phase: CredentialLifecycleMutationPhase.Intent);
        var durableIntent = await _registry.MutateAsync(intent, cancellationToken);
        if (durableIntent.Status is not (CredentialRegistryMutationStatus.Applied or CredentialRegistryMutationStatus.Replayed))
        {
            return FromRegistry(request, durableIntent, CredentialProviderHealthStatus.NeedsRepair, "The durable pre-locator create/import intent could not be recorded.");
        }
        if (durableIntent.Status == CredentialRegistryMutationStatus.Replayed)
        {
            return ReplayProviderOperation(request, await _registry.ReadAsync(cancellationToken), durableIntent.Entry);
        }

        CredentialProviderLocator? locator;
        try
        {
            locator = await _locatorSource.CreateAsync(request.WorkspaceId, request.ReferenceId, request.Reference!.ProviderId, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return await CommitLocatorUncertainAsync(request, "Provider locator creation was canceled after durable intent and has an uncertain outcome.");
        }
        catch (Exception)
        {
            return await CommitLocatorUncertainAsync(request, "Provider locator creation ended without a trustworthy outcome.");
        }
        if (locator is null)
        {
            return await CommitLocatorUncertainAsync(request, "The provider returned no trustworthy private registration locator after durable intent.");
        }

        var locatorPrepared = Mutation(request, CredentialRegistryMutationKind.Register, durableIntent.RegistryRevision!.Value, request.Reference, request.Binding, request.ConsentReference, CredentialProviderHealthStatus.NeedsRepair, locator, false, operationId: DeriveOperationId(request.OperationId, "locator-prepared"), phase: CredentialLifecycleMutationPhase.LocatorPrepared);
        var prepared = await _registry.MutateAsync(locatorPrepared, CancellationToken.None);
        if (prepared.Status != CredentialRegistryMutationStatus.Applied)
        {
            return await CommitLocatorUncertainAsync(request, "Provider locator creation completed but its private registry attachment is uncertain.");
        }

        CredentialProviderResult providerResult;
        try
        {
            providerResult = await _provider.CreateAsync(new CredentialProviderMutationRequest(request.WorkspaceId, request.ReferenceId, request.Reference.ProviderId, request.OperationId, request.ValueByteLength), source, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return await CommitUncertainAsync(request, prepared.RegistryRevision!.Value, "The provider create/import call was canceled after durable intent and has an uncertain outcome.");
        }
        catch (Exception)
        {
            return await CommitUncertainAsync(request, prepared.RegistryRevision!.Value, "The provider create/import call ended without a trustworthy outcome.");
        }
        if (!CredentialPortContractValidator.Validate(providerResult).IsValid)
        {
            return await CommitUncertainAsync(request, prepared.RegistryRevision!.Value, "The provider returned an invalid create/import outcome.");
        }
        if (!providerResult.Succeeded)
        {
            return await ResolveProviderFailureAsync(request, prepared.Entry!, providerResult.Failure!, CredentialProviderHealthStatus.Missing, prepared.RegistryRevision!.Value, cancellationToken);
        }

        return await CompleteHealthAsync(request, prepared.Entry!, CredentialProviderHealthStatus.Available, prepared.RegistryRevision!.Value, "The provider material and value-free registration are committed.", cancellationToken);
    }

    private async Task<CredentialLifecycleResult> ReplaceAsync(CredentialLifecycleRequest request, CredentialRegistryEntry entry, CredentialSecretWriteCallback source, CancellationToken cancellationToken)
    {
        var prepared = await _registry.MutateAsync(Mutation(request, CredentialRegistryMutationKind.UpdatePosture, request.ExpectedRegistryRevision, entry.Reference, null, null, CredentialProviderHealthStatus.NeedsRepair, null, null, phase: CredentialLifecycleMutationPhase.Intent), cancellationToken);
        if (prepared.Status is not (CredentialRegistryMutationStatus.Applied or CredentialRegistryMutationStatus.Replayed))
        {
            return FromRegistry(request, prepared, entry.Health, "The durable rotate/replace intent could not be recorded.");
        }
        if (prepared.Status == CredentialRegistryMutationStatus.Replayed)
        {
            return ReplayProviderOperation(request, await _registry.ReadAsync(cancellationToken), prepared.Entry);
        }

        CredentialProviderResult providerResult;
        try
        {
            providerResult = await _provider.ReplaceAsync(new CredentialProviderMutationRequest(request.WorkspaceId, request.ReferenceId, entry.Reference.ProviderId, request.OperationId, request.ValueByteLength), source, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return await CommitUncertainAsync(request, prepared.RegistryRevision!.Value, "The provider rotate/replace call was canceled after durable intent and has an uncertain outcome.");
        }
        catch (Exception)
        {
            return await CommitUncertainAsync(request, prepared.RegistryRevision!.Value, "The provider rotate/replace call ended without a trustworthy outcome.");
        }
        if (!CredentialPortContractValidator.Validate(providerResult).IsValid)
        {
            return await CommitUncertainAsync(request, prepared.RegistryRevision!.Value, "The provider returned an invalid rotate/replace outcome.");
        }
        if (!providerResult.Succeeded)
        {
            return await ResolveProviderFailureAsync(request, prepared.Entry!, providerResult.Failure!, entry.Health, prepared.RegistryRevision!.Value, cancellationToken);
        }

        return await CompleteHealthAsync(request, prepared.Entry!, CredentialProviderHealthStatus.Available, prepared.RegistryRevision!.Value, "The replacement was proved and the prior value was superseded.", cancellationToken);
    }

    private async Task<CredentialLifecycleResult> DeleteAsync(CredentialLifecycleRequest request, CredentialRegistryEntry entry, CancellationToken cancellationToken)
    {
        var prepared = await _registry.MutateAsync(Mutation(request, CredentialRegistryMutationKind.UpdatePosture, request.ExpectedRegistryRevision, entry.Reference, null, null, CredentialProviderHealthStatus.NeedsRepair, null, null, phase: CredentialLifecycleMutationPhase.Intent), cancellationToken);
        if (prepared.Status is not (CredentialRegistryMutationStatus.Applied or CredentialRegistryMutationStatus.Replayed))
        {
            return FromRegistry(request, prepared, entry.Health, "The durable delete intent could not be recorded.");
        }
        if (prepared.Status == CredentialRegistryMutationStatus.Replayed)
        {
            return ReplayProviderOperation(request, await _registry.ReadAsync(cancellationToken), prepared.Entry);
        }

        CredentialProviderResult providerResult;
        try
        {
            providerResult = await _provider.DeleteAsync(new CredentialProviderDeleteRequest(request.WorkspaceId, request.ReferenceId, entry.Reference.ProviderId, request.OperationId), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            providerResult = CredentialProviderResult.Failed(CredentialFailure.FromCode(CredentialFailureCode.OutcomeUncertain));
        }
        catch (Exception)
        {
            providerResult = CredentialProviderResult.Failed(CredentialFailure.FromCode(CredentialFailureCode.OutcomeUncertain));
        }
        var cleanupUncertain = !CredentialPortContractValidator.Validate(providerResult).IsValid || !providerResult.Succeeded;
        var tombstonePhase = cleanupUncertain ? "tombstone-uncertain" : "tombstone-complete";
        var phase = cleanupUncertain ? CredentialLifecycleMutationPhase.TombstoneUncertain : CredentialLifecycleMutationPhase.TombstoneComplete;
        var detail = cleanupUncertain ? "The tombstone is durable but provider cleanup remains uncertain." : "Provider cleanup and the immutable tombstone are committed.";
        var status = cleanupUncertain ? CredentialLifecycleResultStatus.NeedsRepair : CredentialLifecycleResultStatus.Applied;
        var tombstone = await _registry.MutateAsync(Mutation(request, CredentialRegistryMutationKind.Tombstone, prepared.RegistryRevision!.Value, null, null, null, null, null, null, operationId: DeriveOperationId(request.OperationId, tombstonePhase), phase: phase, terminalStatus: status, terminalDetail: detail), CancellationToken.None);
        if (tombstone.Status is not (CredentialRegistryMutationStatus.Applied or CredentialRegistryMutationStatus.Replayed))
        {
            return Result(request, CredentialLifecycleResultStatus.NeedsRepair, tombstone.RegistryRevision, CredentialProviderHealthStatus.NeedsRepair, [], CredentialFailureCode.OutcomeUncertain, "Provider cleanup ran but the irreversible tombstone could not be proved.");
        }

        return cleanupUncertain
            ? Result(request, CredentialLifecycleResultStatus.NeedsRepair, tombstone.RegistryRevision, CredentialProviderHealthStatus.NeedsRepair, [], CredentialFailureCode.OutcomeUncertain, detail)
            : Result(request, CredentialLifecycleResultStatus.Applied, tombstone.RegistryRevision, CredentialProviderHealthStatus.Missing, [], null, detail);
    }

    private async Task<CredentialLifecycleResult> RepairAsync(CredentialLifecycleRequest request, CredentialRegistryEntry? entry, CredentialRegistryTombstone? tombstone, CancellationToken cancellationToken)
    {
        var prepared = await _registry.MutateAsync(Mutation(request, CredentialRegistryMutationKind.BeginRepair, request.ExpectedRegistryRevision, null, null, null, null, null, null, phase: CredentialLifecycleMutationPhase.Intent), cancellationToken);
        if (prepared.Status is not (CredentialRegistryMutationStatus.Applied or CredentialRegistryMutationStatus.Replayed))
        {
            return FromRegistry(request, prepared, CredentialProviderHealthStatus.NeedsRepair, "The durable explicit repair intent could not be recorded.");
        }
        if (prepared.Status == CredentialRegistryMutationStatus.Replayed)
        {
            return ReplayProviderOperation(request, await _registry.ReadAsync(cancellationToken), null);
        }

        CredentialProviderResult providerResult;
        try
        {
            var providerId = entry?.Reference.ProviderId ?? tombstone!.RepairProviderId!;
            providerResult = await _provider.DeleteAsync(new CredentialProviderDeleteRequest(request.WorkspaceId, request.ReferenceId, providerId, request.OperationId), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return await CommitRepairUncertainAsync(request, prepared.RegistryRevision!.Value, entry is not null, "Explicit repair cleanup was canceled after durable intent and has an uncertain outcome.");
        }
        catch (Exception)
        {
            return await CommitRepairUncertainAsync(request, prepared.RegistryRevision!.Value, entry is not null, "Explicit repair cleanup ended without a trustworthy outcome and will not be retried automatically.");
        }
        if (!CredentialPortContractValidator.Validate(providerResult).IsValid || !providerResult.Succeeded)
        {
            return await CommitRepairUncertainAsync(request, prepared.RegistryRevision!.Value, entry is not null, "Explicit repair cleanup is uncertain and will not be retried automatically.");
        }

        const string CompleteDetail = "Explicit cleanup repair was proved and retained private locator state was removed.";
        var completionKind = entry is null ? CredentialRegistryMutationKind.CompleteRepair : CredentialRegistryMutationKind.Tombstone;
        var completed = await _registry.MutateAsync(Mutation(request, completionKind, prepared.RegistryRevision!.Value, null, null, null, null, null, null, operationId: DeriveOperationId(request.OperationId, "repair-complete"), phase: CredentialLifecycleMutationPhase.RepairComplete, terminalStatus: CredentialLifecycleResultStatus.Applied, terminalDetail: CompleteDetail), CancellationToken.None);
        return completed.Status is CredentialRegistryMutationStatus.Applied or CredentialRegistryMutationStatus.Replayed
            ? Result(request, CredentialLifecycleResultStatus.Applied, completed.RegistryRevision, CredentialProviderHealthStatus.Missing, [], null, CompleteDetail)
            : Result(request, CredentialLifecycleResultStatus.NeedsRepair, completed.RegistryRevision, CredentialProviderHealthStatus.NeedsRepair, [], CredentialFailureCode.OutcomeUncertain, "Provider repair cleanup succeeded but durable repair completion is uncertain.");
    }

    private async Task<CredentialLifecycleResult> ReconcileRepairAsync(CredentialLifecycleRequest request, long revision, CancellationToken cancellationToken)
    {
        const string Detail = "The interrupted repair intent was conservatively reconciled as uncertain without claiming provider success; a new explicit repair is required.";
        var mutation = Mutation(request, CredentialRegistryMutationKind.ReconcileRepair, revision, null, null, null, null, null, null, phase: CredentialLifecycleMutationPhase.RepairReconciledUncertain, terminalStatus: CredentialLifecycleResultStatus.NeedsRepair, terminalDetail: Detail, lifecycleIntentOperationId: request.InterruptedRepairOperationId);
        var reconciled = await _registry.MutateAsync(mutation, cancellationToken);
        if (reconciled.Failure?.Code == CredentialFailureCode.Unauthorized)
        {
            return Result(request, CredentialLifecycleResultStatus.Denied, reconciled.RegistryRevision, CredentialProviderHealthStatus.NeedsRepair, [], CredentialFailureCode.Unauthorized, "The closed durable registry boundary denied repair reconciliation.");
        }
        return reconciled.Status is CredentialRegistryMutationStatus.Applied or CredentialRegistryMutationStatus.Replayed
            ? Result(request, reconciled.Status == CredentialRegistryMutationStatus.Replayed ? CredentialLifecycleResultStatus.Replayed : CredentialLifecycleResultStatus.NeedsRepair, reconciled.RegistryRevision, CredentialProviderHealthStatus.NeedsRepair, [], reconciled.Status == CredentialRegistryMutationStatus.Replayed ? null : CredentialFailureCode.OutcomeUncertain, Detail)
            : FromRegistry(request, reconciled, CredentialProviderHealthStatus.NeedsRepair, "The interrupted repair intent could not be reconciled durably.");
    }

    private async Task<CredentialLifecycleResult> TestAsync(CredentialLifecycleRequest request, CredentialRegistryEntry entry, CancellationToken cancellationToken)
    {
        CredentialProviderHealthResult? health;
        try
        {
            health = await _provider.GetHealthAsync(new CredentialProviderUseRequest(request.WorkspaceId, request.ReferenceId, entry.Reference.ProviderId, request.OperationId), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Result(request, CredentialLifecycleResultStatus.Unavailable, request.ExpectedRegistryRevision, entry.Health, [], CredentialFailureCode.Unavailable, "The provider health call ended without a trustworthy outcome.");
        }
        if (health is null)
        {
            return Result(request, CredentialLifecycleResultStatus.Unavailable, request.ExpectedRegistryRevision, CredentialProviderHealthStatus.Unavailable, [], CredentialFailureCode.Unavailable, "The provider returned no safe health outcome.");
        }
        var status = health.Failure is null ? CredentialLifecycleResultStatus.Applied : health.Status == CredentialProviderHealthStatus.Corrupt ? CredentialLifecycleResultStatus.Failed : CredentialLifecycleResultStatus.Unavailable;
        var detail = health.Failure is null ? "Safe provider health was tested without invoking a credential-bearing external effect." : health.Status == CredentialProviderHealthStatus.Corrupt ? "Provider health testing proved corrupt credential material without exposing it." : "Provider health testing could not establish a trustworthy provider posture.";
        var mutation = Mutation(request, CredentialRegistryMutationKind.SetHealth, request.ExpectedRegistryRevision, null, null, null, health.Status, null, null, phase: CredentialLifecycleMutationPhase.MetadataComplete, terminalStatus: status, terminalDetail: detail);
        var committed = await _registry.MutateAsync(mutation, CancellationToken.None);
        return committed.Status is CredentialRegistryMutationStatus.Applied or CredentialRegistryMutationStatus.Replayed
            ? Result(request, committed.Status == CredentialRegistryMutationStatus.Replayed && health.Failure is null ? CredentialLifecycleResultStatus.Replayed : status, committed.RegistryRevision, health.Status, [], health.Failure?.Code, detail)
            : FromRegistry(request, committed, entry.Health, "Safe provider health could not be committed.");
    }

    private async Task<CredentialLifecycleResult> ApplyMetadataAsync(CredentialLifecycleRequest request, CredentialRegistryEntry entry, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> activeRuns = [];
        if (request.Kind is CredentialLifecycleOperationKind.Expire or CredentialLifecycleOperationKind.Revoke or CredentialLifecycleOperationKind.Disable)
        {
            var activeRunCapture = await CaptureActiveRunsAsync(entry.Binding, cancellationToken);
            if (!activeRunCapture.Succeeded)
            {
                return Result(request, CredentialLifecycleResultStatus.Unavailable, request.ExpectedRegistryRevision, entry.Health, [], CredentialFailureCode.Unavailable, "The exact bounded active-run impact could not be captured, so restrictive posture was not changed.");
            }
            activeRuns = activeRunCapture.Runs;
        }

        const string Detail = "The value-free lifecycle metadata transition is committed and grants no authority.";
        CredentialRegistryMutation mutation;
        if (request.Kind == CredentialLifecycleOperationKind.Bind)
        {
            mutation = Mutation(request, CredentialRegistryMutationKind.Bind, request.ExpectedRegistryRevision, null, request.Binding, null, null, null, null, phase: CredentialLifecycleMutationPhase.MetadataComplete, terminalStatus: CredentialLifecycleResultStatus.Applied, terminalDetail: Detail);
        }
        else if (request.Kind == CredentialLifecycleOperationKind.Consent)
        {
            mutation = Mutation(request, CredentialRegistryMutationKind.Consent, request.ExpectedRegistryRevision, null, null, request.ConsentReference, null, null, true, phase: CredentialLifecycleMutationPhase.MetadataComplete, terminalStatus: CredentialLifecycleResultStatus.Applied, terminalDetail: Detail);
        }
        else
        {
            var status = request.Kind switch
            {
                CredentialLifecycleOperationKind.Expire => CredentialLifecycleStatus.Expired,
                CredentialLifecycleOperationKind.Revoke => CredentialLifecycleStatus.Revoked,
                _ => CredentialLifecycleStatus.Disabled
            };
            var health = request.Kind switch
            {
                CredentialLifecycleOperationKind.Expire => CredentialProviderHealthStatus.Expired,
                CredentialLifecycleOperationKind.Revoke => CredentialProviderHealthStatus.Revoked,
                _ => CredentialProviderHealthStatus.Disabled
            };
            var reference = entry.Reference with { Status = status, UpdatedAtUtc = request.RequestedAtUtc };
            mutation = Mutation(request, CredentialRegistryMutationKind.UpdatePosture, request.ExpectedRegistryRevision, reference, null, null, health, null, null, phase: CredentialLifecycleMutationPhase.MetadataComplete, activeRuns: activeRuns, terminalStatus: CredentialLifecycleResultStatus.Applied, terminalDetail: Detail);
        }

        var committed = await _registry.MutateAsync(mutation, cancellationToken);
        if (committed.Status is not (CredentialRegistryMutationStatus.Applied or CredentialRegistryMutationStatus.Replayed))
        {
            return FromRegistry(request, committed, entry.Health, "The metadata-only lifecycle transition could not be committed.");
        }

        return Result(request, committed.Status == CredentialRegistryMutationStatus.Replayed ? CredentialLifecycleResultStatus.Replayed : CredentialLifecycleResultStatus.Applied, committed.RegistryRevision, committed.Entry?.Health ?? entry.Health, activeRuns, null, Detail);
    }

    private async Task<CredentialLifecycleResult> ResolveProviderFailureAsync(CredentialLifecycleRequest request, CredentialRegistryEntry preparedEntry, CredentialFailure failure, CredentialProviderHealthStatus rollbackHealth, long revision, CancellationToken cancellationToken)
    {
        if (failure.Code == CredentialFailureCode.OutcomeUncertain)
        {
            return await CommitUncertainAsync(request, revision, "Provider mutation is uncertain and will not be retried automatically.");
        }

        const string RollbackDetail = "The provider proved failure and the previous safe posture was preserved.";
        var rollback = await _registry.MutateAsync(Mutation(request, CredentialRegistryMutationKind.SetHealth, revision, null, null, null, rollbackHealth, null, null, operationId: DeriveOperationId(request.OperationId, "rollback"), phase: CredentialLifecycleMutationPhase.Rollback, terminalStatus: CredentialLifecycleResultStatus.Failed, terminalDetail: RollbackDetail), CancellationToken.None);
        if (rollback.Status is not (CredentialRegistryMutationStatus.Applied or CredentialRegistryMutationStatus.Replayed))
        {
            return Result(request, CredentialLifecycleResultStatus.NeedsRepair, rollback.RegistryRevision, CredentialProviderHealthStatus.NeedsRepair, [], CredentialFailureCode.OutcomeUncertain, "The provider proved failure but registry rollback posture could not be committed.");
        }
        return Result(request, CredentialLifecycleResultStatus.Failed, rollback.RegistryRevision, rollbackHealth, [], failure.Code, RollbackDetail);
    }

    private async Task<CredentialLifecycleResult> CompleteHealthAsync(CredentialLifecycleRequest request, CredentialRegistryEntry preparedEntry, CredentialProviderHealthStatus health, long revision, string detail, CancellationToken cancellationToken)
    {
        var completed = await _registry.MutateAsync(Mutation(request, CredentialRegistryMutationKind.SetHealth, revision, null, null, null, health, null, null, operationId: DeriveOperationId(request.OperationId, "complete"), phase: CredentialLifecycleMutationPhase.Complete, terminalStatus: CredentialLifecycleResultStatus.Applied, terminalDetail: detail), CancellationToken.None);
        return completed.Status is CredentialRegistryMutationStatus.Applied or CredentialRegistryMutationStatus.Replayed
            ? Result(request, CredentialLifecycleResultStatus.Applied, completed.RegistryRevision, health, [], null, detail)
            : Result(request, CredentialLifecycleResultStatus.NeedsRepair, completed.RegistryRevision, CredentialProviderHealthStatus.NeedsRepair, [], CredentialFailureCode.OutcomeUncertain, "The provider mutation succeeded but registry completion is uncertain.");
    }

    private async Task<CredentialLifecycleResult> CommitUncertainAsync(CredentialLifecycleRequest request, long revision, string detail)
    {
        var uncertain = await _registry.MutateAsync(Mutation(request, CredentialRegistryMutationKind.SetHealth, revision, null, null, null, CredentialProviderHealthStatus.NeedsRepair, null, null, operationId: DeriveOperationId(request.OperationId, "uncertain"), phase: CredentialLifecycleMutationPhase.Uncertain, terminalStatus: CredentialLifecycleResultStatus.NeedsRepair, terminalDetail: detail), CancellationToken.None);
        return Result(request, CredentialLifecycleResultStatus.NeedsRepair, uncertain.RegistryRevision ?? revision, CredentialProviderHealthStatus.NeedsRepair, [], CredentialFailureCode.OutcomeUncertain, detail);
    }

    private async Task<CredentialLifecycleResult> CommitRepairUncertainAsync(CredentialLifecycleRequest request, long revision, bool preparedRegistration, string detail)
    {
        var kind = preparedRegistration ? CredentialRegistryMutationKind.Tombstone : CredentialRegistryMutationKind.RecordRepairUncertain;
        var uncertain = await _registry.MutateAsync(Mutation(request, kind, revision, null, null, null, null, null, null, operationId: DeriveOperationId(request.OperationId, "repair-uncertain"), phase: CredentialLifecycleMutationPhase.RepairUncertain, terminalStatus: CredentialLifecycleResultStatus.NeedsRepair, terminalDetail: detail), CancellationToken.None);
        return Result(request, CredentialLifecycleResultStatus.NeedsRepair, uncertain.RegistryRevision ?? revision, CredentialProviderHealthStatus.NeedsRepair, [], CredentialFailureCode.OutcomeUncertain, detail);
    }

    private async Task<CredentialLifecycleResult> CommitLocatorUncertainAsync(CredentialLifecycleRequest request, string detail)
    {
        var read = await _registry.ReadAsync(CancellationToken.None);
        if (!read.Succeeded || read.RegistryRevision is null)
        {
            return Result(request, CredentialLifecycleResultStatus.NeedsRepair, null, CredentialProviderHealthStatus.NeedsRepair, [], CredentialFailureCode.OutcomeUncertain, detail);
        }
        var uncertain = await _registry.MutateAsync(Mutation(request, CredentialRegistryMutationKind.RecordLocatorUncertain, read.RegistryRevision.Value, null, null, null, null, null, null, operationId: DeriveOperationId(request.OperationId, "locator-uncertain"), phase: CredentialLifecycleMutationPhase.LocatorUncertain, terminalStatus: CredentialLifecycleResultStatus.NeedsRepair, terminalDetail: detail), CancellationToken.None);
        return Result(request, CredentialLifecycleResultStatus.NeedsRepair, uncertain.RegistryRevision ?? read.RegistryRevision, CredentialProviderHealthStatus.NeedsRepair, [], CredentialFailureCode.OutcomeUncertain, detail);
    }

    private async Task<bool> ValidateConfirmedPreviewAsync(CredentialLifecycleRequest request, CredentialCapabilityBinding binding, CredentialContractHash bindingHash, long targetRevision, CancellationToken cancellationToken)
    {
        var preview = request.Preview!;
        if (!request.Confirmed || preview.Status is not (CredentialLifecyclePreviewStatus.Ready or CredentialLifecyclePreviewStatus.Replayed) || !request.OperationId.Equals(preview.OperationId) || preview.Kind != request.Kind || !request.ReferenceId.Equals(preview.ReferenceId) || !string.Equals(preview.WorkspaceId, request.WorkspaceId, StringComparison.Ordinal) || !string.Equals(preview.ActorId, request.ActorId, StringComparison.Ordinal) || preview.RegistryRevision != request.ExpectedRegistryRevision)
        {
            return false;
        }
        var dependents = await _dependentIndex.CaptureAsync(cancellationToken);
        var finalized = dependents.Status == CapabilityDependentIndexStatus.Available ? await _dependentIndex.CaptureAsync(cancellationToken) : dependents;
        if (dependents.Status != CapabilityDependentIndexStatus.Available || finalized.Status != CapabilityDependentIndexStatus.Available || !string.Equals(dependents.Hash, finalized.Hash, StringComparison.Ordinal) || !string.Equals(finalized.Hash, preview.DependentSetRevision, StringComparison.Ordinal))
        {
            return false;
        }
        var impacts = ProjectImpacts(binding, finalized.Dependents);
        return string.Equals(ComputePreviewRevision(new CredentialLifecyclePreviewRequest(request.OperationId, request.Kind, request.ReferenceId, request.WorkspaceId, request.ActorId, request.ExpectedRegistryRevision, request.InterruptedRepairOperationId), bindingHash, targetRevision, finalized.Hash, impacts), preview.PreviewRevision, StringComparison.Ordinal);
    }

    private static IReadOnlyList<CredentialLifecycleImpact> ProjectImpacts(CredentialCapabilityBinding binding, IReadOnlyList<CapabilityDependent> dependents)
    {
        return dependents.Where(dependent => dependent.Manifest.Required.Concat(dependent.Manifest.Optional).Any(requirement => requirement.CapabilityId.Equals(binding.Capability.Id))).OrderBy(dependent => dependent.Kind).ThenBy(dependent => dependent.Identity, StringComparer.Ordinal).Select(dependent => new CredentialLifecycleImpact(dependent.Kind, dependent.Identity, dependent.Revision, dependent.AuthorityPosture)).ToArray();
    }

    private async Task<(bool Succeeded, IReadOnlyList<string> Runs)> CaptureActiveRunsAsync(CredentialCapabilityBinding binding, CancellationToken cancellationToken)
    {
        IReadOnlyList<string>? runs;
        try
        {
            runs = await _activeRunIndex.CaptureAsync(binding, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return (false, []);
        }
        if (runs is null || runs.Count > MaximumActiveRuns || runs.Any(run => string.IsNullOrWhiteSpace(run) || run.Length > 256 || run.Any(character => character < (char)0x20 || character == (char)0x7f)) || runs.Distinct(StringComparer.Ordinal).Count() != runs.Count)
        {
            return (false, []);
        }
        return (true, Array.AsReadOnly(runs.Order(StringComparer.Ordinal).ToArray()));
    }

    private static CredentialLifecycleResult? ValidateRequest(CredentialLifecycleRequest request, CredentialSecretWriteCallback? source)
    {
        if (!Enum.IsDefined(request.Kind) || request.OperationId is null || request.ReferenceId is null || request.ExpectedRegistryRevision < 0 || request.RequestedAtUtc.Offset != TimeSpan.Zero || !IsSafe(request.WorkspaceId, 256) || !IsSafe(request.ActorId, 128))
        {
            return Result(request, CredentialLifecycleResultStatus.Invalid, null, CredentialProviderHealthStatus.Unavailable, [], CredentialFailureCode.InvalidRequest, "The lifecycle request is invalid.");
        }
        if (IsValueBearing(request.Kind) != (source is not null) || IsValueBearing(request.Kind) && request.ValueByteLength is < 1 or > CredentialContractLimits.MaxCredentialBytes || !IsValueBearing(request.Kind) && request.ValueByteLength != 0)
        {
            return Result(request, CredentialLifecycleResultStatus.Invalid, null, CredentialProviderHealthStatus.Unavailable, [], CredentialFailureCode.InvalidRequest, "The callback-only value input does not match the lifecycle operation.");
        }
        if ((request.Kind == CredentialLifecycleOperationKind.ReconcileRepair) != (request.InterruptedRepairOperationId is not null))
        {
            return Result(request, CredentialLifecycleResultStatus.Invalid, null, CredentialProviderHealthStatus.Unavailable, [], CredentialFailureCode.InvalidRequest, "Only repair reconciliation may identify one interrupted repair operation.");
        }
        return null;
    }

    private static CredentialLifecycleResult? ValidateTransition(CredentialLifecycleRequest request, CredentialRegistryEntry? entry, CredentialRegistryTombstone? tombstone, bool preparedCreateRepair, CredentialRegistryOperationEvidence? interruptedRepair)
    {
        if (request.Kind is CredentialLifecycleOperationKind.Create or CredentialLifecycleOperationKind.Import)
        {
            var valid = entry is null && tombstone is null && request.Reference is not null && request.Binding is not null && request.ConsentReference is not null && request.Reference.Id.Equals(request.ReferenceId) && request.Binding.ReferenceId.Equals(request.ReferenceId) && request.Reference.Status == CredentialLifecycleStatus.Active;
            return valid ? null : Result(request, entry is null ? CredentialLifecycleResultStatus.Invalid : CredentialLifecycleResultStatus.Conflict, request.ExpectedRegistryRevision, entry?.Health ?? CredentialProviderHealthStatus.Missing, [], CredentialFailureCode.Conflict, "Create/import requires a new untombstoned reference, exact binding, and non-authorizing consent reference.");
        }
        if (request.Kind == CredentialLifecycleOperationKind.Repair)
        {
            var valid = preparedCreateRepair && entry is not null || entry is null && tombstone is { NeedsRepair: true, RepairBinding: not null, RepairProviderId: not null };
            return valid ? null : Result(request, CredentialLifecycleResultStatus.Conflict, request.ExpectedRegistryRevision, CredentialProviderHealthStatus.Missing, [], CredentialFailureCode.Conflict, "Repair requires one exact prepared registration or repair-required tombstone.");
        }
        if (request.Kind == CredentialLifecycleOperationKind.ReconcileRepair)
        {
            var valid = interruptedRepair is not null && (preparedCreateRepair && entry is not null || entry is null && tombstone is { NeedsRepair: true, RepairBinding: not null, RepairProviderId: not null });
            return valid ? null : Result(request, CredentialLifecycleResultStatus.Conflict, request.ExpectedRegistryRevision, CredentialProviderHealthStatus.NeedsRepair, [], CredentialFailureCode.Conflict, "Repair reconciliation requires the exact unresolved repair intent and retained private cleanup state.");
        }
        if (entry is null)
        {
            return Result(request, CredentialLifecycleResultStatus.NotFound, request.ExpectedRegistryRevision, CredentialProviderHealthStatus.Missing, [], CredentialFailureCode.NotFound, "The credential reference was not found.");
        }
        if (request.Kind == CredentialLifecycleOperationKind.Bind && (request.Binding is null || !request.Binding.ReferenceId.Equals(request.ReferenceId)) || request.Kind == CredentialLifecycleOperationKind.Consent && request.ConsentReference is null)
        {
            return Result(request, CredentialLifecycleResultStatus.Invalid, request.ExpectedRegistryRevision, entry.Health, [], CredentialFailureCode.InvalidRequest, "The binding or consent transition is incomplete.");
        }
        if (request.Kind is CredentialLifecycleOperationKind.Rotate or CredentialLifecycleOperationKind.Replace or CredentialLifecycleOperationKind.Disable && entry.Reference.Status != CredentialLifecycleStatus.Active || request.Kind == CredentialLifecycleOperationKind.Expire && entry.Reference.Status is CredentialLifecycleStatus.Expired or CredentialLifecycleStatus.Revoked || request.Kind == CredentialLifecycleOperationKind.Revoke && entry.Reference.Status == CredentialLifecycleStatus.Revoked)
        {
            return Result(request, CredentialLifecycleResultStatus.Conflict, request.ExpectedRegistryRevision, entry.Health, [], CredentialFailureCode.Conflict, "The current lifecycle state does not permit the requested transition.");
        }
        if (request.Kind == CredentialLifecycleOperationKind.Test && (entry.Reference.Status != CredentialLifecycleStatus.Active || entry.Health is CredentialProviderHealthStatus.Revoked or CredentialProviderHealthStatus.Disabled or CredentialProviderHealthStatus.Expired))
        {
            return Result(request, CredentialLifecycleResultStatus.Conflict, request.ExpectedRegistryRevision, entry.Health, [], CredentialFailureCode.Conflict, "Safe health testing cannot widen a restrictive credential posture.");
        }
        return null;
    }

    private static CredentialLifecycleResult? ValidateWorkspaceBinding(CredentialLifecycleRequest request, CredentialRegistryEntry? entry, CredentialRegistryTombstone? tombstone)
    {
        var binding = entry?.Binding ?? tombstone?.RepairBinding ?? request.Binding;
        return binding is null || string.Equals(binding.Scope.WorkspaceId, request.WorkspaceId, StringComparison.Ordinal) ? null : Result(request, CredentialLifecycleResultStatus.Conflict, request.ExpectedRegistryRevision, entry?.Health ?? CredentialProviderHealthStatus.Missing, [], CredentialFailureCode.Conflict, "The request workspace does not match the credential binding.");
    }

    private static bool ValidatePreviewRequest(CredentialLifecyclePreviewRequest request) => request.OperationId is not null && request.ReferenceId is not null && RequiresPreview(request.Kind) && IsSafe(request.WorkspaceId, 256) && IsSafe(request.ActorId, 128) && request.ExpectedRegistryRevision >= 0 && ((request.Kind == CredentialLifecycleOperationKind.ReconcileRepair) == (request.InterruptedRepairOperationId is not null));
    private static bool RequiresPreview(CredentialLifecycleOperationKind kind) => kind is CredentialLifecycleOperationKind.Rotate or CredentialLifecycleOperationKind.Expire or CredentialLifecycleOperationKind.Revoke or CredentialLifecycleOperationKind.Replace or CredentialLifecycleOperationKind.Disable or CredentialLifecycleOperationKind.Delete or CredentialLifecycleOperationKind.Repair or CredentialLifecycleOperationKind.ReconcileRepair;
    private static bool RequiresUser(CredentialLifecycleOperationKind kind) => kind == CredentialLifecycleOperationKind.Consent || RequiresPreview(kind);
    private static bool IsValueBearing(CredentialLifecycleOperationKind kind) => kind is CredentialLifecycleOperationKind.Create or CredentialLifecycleOperationKind.Import or CredentialLifecycleOperationKind.Rotate or CredentialLifecycleOperationKind.Replace;
    private static bool HasProviderMutation(CredentialLifecycleOperationKind kind) => IsValueBearing(kind) || kind is CredentialLifecycleOperationKind.Delete or CredentialLifecycleOperationKind.Repair;
    private static bool IsSafe(string? value, int maximum) => value is not null && value.Length > 0 && value.Length <= maximum && value.All(character => character >= (char)0x20 && character != (char)0x7f);

    private static CredentialRegistryMutation Mutation(CredentialLifecycleRequest request, CredentialRegistryMutationKind kind, long expectedRevision, CredentialReference? reference, CredentialCapabilityBinding? binding, CredentialContractId? consentReference, CredentialProviderHealthStatus? health, CredentialProviderLocator? locator, bool? consentGranted, CredentialContractId? operationId = null, CredentialLifecycleMutationPhase? phase = null, IReadOnlyList<string>? activeRuns = null, CredentialLifecycleResultStatus? terminalStatus = null, string? terminalDetail = null, CredentialContractId? lifecycleIntentOperationId = null)
    {
        var audit = phase == CredentialLifecycleMutationPhase.Intent
            ? new CredentialLifecycleAuditPayload(AuditSchema.Actions.CredentialLifecycleIntent, AuditSchema.Outcomes.Started, "Credential lifecycle intent was durably recorded before any provider mutation.")
            : terminalStatus is null || terminalDetail is null ? null : new CredentialLifecycleAuditPayload(AuditSchema.Actions.CredentialLifecycleOutcome, AuditOutcome(terminalStatus.Value), terminalDetail);
        return new CredentialRegistryMutation(kind, operationId ?? request.OperationId, expectedRevision, request.ReferenceId, reference, binding, consentReference, health, locator, consentGranted, (int)request.Kind, request.ActorId, request.Preview?.PreviewRevision, ComputeLifecycleRequestHash(request), phase, phase is null ? null : lifecycleIntentOperationId ?? request.OperationId, activeRuns, request.WorkspaceId, audit);
    }

    private static CredentialContractId DeriveOperationId(CredentialContractId operationId, string phase)
    {
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"credential-lifecycle-phase-v1\n{operationId.Value}\n{phase}"))).ToLowerInvariant();
        return CredentialContractId.TryParse("op_" + digest, out var parsed, out _) ? parsed! : throw new InvalidOperationException("The lifecycle phase operation identity is invalid.");
    }

    private static string ComputePreviewRevision(CredentialLifecyclePreviewRequest request, CredentialContractHash bindingHash, long targetRevision, string dependentRevision, IReadOnlyList<CredentialLifecycleImpact> impacts)
    {
        var builder = new StringBuilder("credential-lifecycle-preview-v1\n");
        Append(builder, request.OperationId.Value);
        Append(builder, ((int)request.Kind).ToString(System.Globalization.CultureInfo.InvariantCulture));
        Append(builder, request.ReferenceId.Value);
        Append(builder, request.WorkspaceId);
        Append(builder, request.ActorId);
        Append(builder, request.ExpectedRegistryRevision.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (request.Kind == CredentialLifecycleOperationKind.ReconcileRepair)
        {
            Append(builder, request.InterruptedRepairOperationId!.Value);
        }
        Append(builder, bindingHash.Value);
        Append(builder, targetRevision.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Append(builder, dependentRevision);
        foreach (var impact in impacts)
        {
            Append(builder, ((int)impact.Kind).ToString(System.Globalization.CultureInfo.InvariantCulture));
            Append(builder, impact.Identity);
            Append(builder, impact.Revision);
            Append(builder, ((int)impact.AuthorityPosture).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        return "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    private static void Append(StringBuilder builder, string value) => builder.Append(value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(':').Append(value).Append('\n');

    private CredentialLifecycleResult ReplayProviderOperation(CredentialLifecycleRequest request, CredentialRegistryReadResult read, CredentialRegistryEntry? entry)
    {
        var completionId = DeriveOperationId(request.OperationId, "complete");
        var rollbackId = DeriveOperationId(request.OperationId, "rollback");
        var tombstoneCompletionId = DeriveOperationId(request.OperationId, "tombstone-complete");
        var tombstoneUncertainId = DeriveOperationId(request.OperationId, "tombstone-uncertain");
        var repairCompletionId = DeriveOperationId(request.OperationId, "repair-complete");
        var uncertainId = DeriveOperationId(request.OperationId, "uncertain");
        var repairUncertainId = DeriveOperationId(request.OperationId, "repair-uncertain");
        var locatorUncertainId = DeriveOperationId(request.OperationId, "locator-uncertain");
        if (request.Kind == CredentialLifecycleOperationKind.Repair && (HasExactPhase(read, request, repairCompletionId, CredentialLifecycleMutationPhase.RepairComplete, CredentialRegistryMutationKind.CompleteRepair, null) || HasExactTombstonePhase(read, request, repairCompletionId, CredentialLifecycleMutationPhase.RepairComplete)))
        {
            return Result(request, CredentialLifecycleResultStatus.Replayed, read.RegistryRevision, CredentialProviderHealthStatus.Missing, [], null, "The exact explicit cleanup repair was already committed.");
        }
        if (HasExactPhase(read, request, completionId, CredentialLifecycleMutationPhase.Complete, CredentialRegistryMutationKind.SetHealth, CredentialProviderHealthStatus.Available) || HasExactTombstonePhase(read, request, tombstoneCompletionId, CredentialLifecycleMutationPhase.TombstoneComplete))
        {
            return Result(request, CredentialLifecycleResultStatus.Replayed, read.RegistryRevision, entry?.Health ?? CredentialProviderHealthStatus.Missing, [], null, "The exact value-bearing lifecycle operation was already committed.");
        }
        if (HasExactPhase(read, request, rollbackId, CredentialLifecycleMutationPhase.Rollback, CredentialRegistryMutationKind.SetHealth, null))
        {
            return Result(request, CredentialLifecycleResultStatus.Replayed, read.RegistryRevision, entry?.Health ?? CredentialProviderHealthStatus.Missing, [], null, "The exact value-bearing lifecycle operation already proved failure and preserved the prior posture.");
        }
        if (HasExactTombstonePhase(read, request, tombstoneUncertainId, CredentialLifecycleMutationPhase.TombstoneUncertain))
        {
            return Result(request, CredentialLifecycleResultStatus.NeedsRepair, read.RegistryRevision, CredentialProviderHealthStatus.NeedsRepair, [], CredentialFailureCode.OutcomeUncertain, "The exact delete operation retained a repair-required tombstone and will not retry cleanup automatically.");
        }
        if (HasExactPhase(read, request, uncertainId, CredentialLifecycleMutationPhase.Uncertain, CredentialRegistryMutationKind.SetHealth, CredentialProviderHealthStatus.NeedsRepair) || HasExactPhase(read, request, repairUncertainId, CredentialLifecycleMutationPhase.RepairUncertain, CredentialRegistryMutationKind.RecordRepairUncertain, null) || HasExactTombstonePhase(read, request, repairUncertainId, CredentialLifecycleMutationPhase.RepairUncertain) || HasExactPhase(read, request, locatorUncertainId, CredentialLifecycleMutationPhase.LocatorUncertain, CredentialRegistryMutationKind.RecordLocatorUncertain, null))
        {
            return Result(request, CredentialLifecycleResultStatus.NeedsRepair, read.RegistryRevision, CredentialProviderHealthStatus.NeedsRepair, [], CredentialFailureCode.OutcomeUncertain, "The exact provider operation has a durable uncertain outcome and will not be retried automatically.");
        }
        if (request.Kind == CredentialLifecycleOperationKind.Repair && read.Operations.Any(operation => operation.ReferenceId.Equals(request.ReferenceId) && operation.LifecycleIntentOperationId?.Equals(request.OperationId) == true && operation.LifecyclePhase == CredentialLifecycleMutationPhase.RepairReconciledUncertain))
        {
            return Result(request, CredentialLifecycleResultStatus.NeedsRepair, read.RegistryRevision, CredentialProviderHealthStatus.NeedsRepair, [], CredentialFailureCode.OutcomeUncertain, "The interrupted repair intent was explicitly reconciled as uncertain and will not retry provider cleanup automatically.");
        }
        return Result(request, CredentialLifecycleResultStatus.NeedsRepair, read.RegistryRevision, entry?.Health ?? CredentialProviderHealthStatus.NeedsRepair, [], CredentialFailureCode.OutcomeUncertain, "A durable provider-mutation intent lacks a proved terminal outcome and will not be retried automatically.");
    }

    private static bool HasExactTombstonePhase(CredentialRegistryReadResult read, CredentialLifecycleRequest request, CredentialContractId operationId, CredentialLifecycleMutationPhase phase)
    {
        return read.Tombstones.Any(tombstone => tombstone.ReferenceId.Equals(request.ReferenceId) && tombstone.OperationId.Equals(operationId)) && HasExactPhase(read, request, operationId, phase, CredentialRegistryMutationKind.Tombstone, null);
    }

    private static bool HasExactPhase(CredentialRegistryReadResult read, CredentialLifecycleRequest request, CredentialContractId operationId, CredentialLifecycleMutationPhase phase, CredentialRegistryMutationKind kind, CredentialProviderHealthStatus? health)
    {
        var evidence = read.Operations.SingleOrDefault(operation => operation.OperationId.Equals(operationId));
        return evidence is not null && evidence.Kind == (int)kind && evidence.ReferenceId.Equals(request.ReferenceId) && evidence.LifecyclePhase == phase && evidence.LifecycleIntentOperationId?.Equals(request.OperationId) == true && (health is null || evidence.ResultHealth == health) && MatchesLifecycleEvidence(evidence, request);
    }

    private static bool MatchesLifecycleEvidence(CredentialRegistryOperationEvidence evidence, CredentialLifecycleRequest request)
    {
        return evidence.LifecycleOperation == (int)request.Kind && string.Equals(evidence.WorkspaceId, request.WorkspaceId, StringComparison.Ordinal) && string.Equals(evidence.ActorId, request.ActorId, StringComparison.Ordinal) && string.Equals(evidence.PreviewHash, request.Preview?.PreviewRevision, StringComparison.Ordinal) && string.Equals(evidence.LifecycleRequestHash, ComputeLifecycleRequestHash(request), StringComparison.Ordinal);
    }

    private static bool HasUnresolvedCreateIntent(CredentialRegistryReadResult read, CredentialReferenceId referenceId)
    {
        return read.Operations.Where(operation => operation.ReferenceId.Equals(referenceId) && operation.Kind == (int)CredentialRegistryMutationKind.BeginCreate && operation.LifecyclePhase == CredentialLifecycleMutationPhase.Intent).Any(intent => !read.Operations.Any(operation => operation.LifecycleIntentOperationId?.Equals(intent.OperationId) == true && operation.LifecyclePhase is CredentialLifecycleMutationPhase.Complete or CredentialLifecycleMutationPhase.Rollback));
    }

    private static bool IsPreparedCreateRepairCandidate(CredentialRegistryReadResult read, CredentialReferenceId referenceId)
    {
        var entry = read.Entries.SingleOrDefault(candidate => candidate.Reference.Id.Equals(referenceId));
        var intent = read.Operations.SingleOrDefault(operation => operation.ReferenceId.Equals(referenceId) && operation.Kind == (int)CredentialRegistryMutationKind.BeginCreate && operation.LifecyclePhase == CredentialLifecycleMutationPhase.Intent);
        return entry?.Health == CredentialProviderHealthStatus.NeedsRepair && intent is not null && read.Operations.Any(operation => operation.ReferenceId.Equals(referenceId) && operation.Kind == (int)CredentialRegistryMutationKind.Register && operation.LifecyclePhase == CredentialLifecycleMutationPhase.LocatorPrepared && operation.LifecycleIntentOperationId?.Equals(intent.OperationId) == true) && !read.Operations.Any(operation => operation.LifecycleIntentOperationId?.Equals(intent.OperationId) == true && operation.LifecyclePhase is CredentialLifecycleMutationPhase.Complete or CredentialLifecycleMutationPhase.Rollback);
    }

    private static bool HasUnresolvedRepairIntent(CredentialRegistryReadResult read, CredentialReferenceId referenceId) => FindUnresolvedRepairIntent(read, referenceId, null) is not null;

    private static CredentialRegistryOperationEvidence? FindUnresolvedRepairIntent(CredentialRegistryReadResult read, CredentialReferenceId referenceId, CredentialContractId? operationId)
    {
        return read.Operations.Where(operation => operation.ReferenceId.Equals(referenceId) && operation.Kind == (int)CredentialRegistryMutationKind.BeginRepair && operation.LifecyclePhase == CredentialLifecycleMutationPhase.Intent && (operationId is null || operation.OperationId.Equals(operationId))).SingleOrDefault(intent => !read.Operations.Any(operation => operation.LifecycleIntentOperationId?.Equals(intent.OperationId) == true && operation.LifecyclePhase is CredentialLifecycleMutationPhase.RepairComplete or CredentialLifecycleMutationPhase.RepairUncertain or CredentialLifecycleMutationPhase.RepairReconciledUncertain));
    }

    private static string ComputeLifecycleRequestHash(CredentialLifecycleRequest request)
    {
        _ = CredentialContractJson.TrySerialize(request.Reference, out var referenceJson, out _);
        _ = CredentialContractJson.TrySerialize(request.Binding, out var bindingJson, out _);
        var builder = new StringBuilder("credential-lifecycle-request-v1\n");
        Append(builder, ((int)request.Kind).ToString(System.Globalization.CultureInfo.InvariantCulture));
        Append(builder, request.OperationId.Value);
        Append(builder, request.ReferenceId.Value);
        Append(builder, request.WorkspaceId);
        Append(builder, request.ActorId);
        Append(builder, request.ExpectedRegistryRevision.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Append(builder, request.RequestedAtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        Append(builder, request.ValueByteLength.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Append(builder, referenceJson ?? string.Empty);
        Append(builder, bindingJson ?? string.Empty);
        Append(builder, request.ConsentReference?.Value ?? string.Empty);
        Append(builder, request.Preview?.PreviewRevision ?? string.Empty);
        Append(builder, request.Confirmed ? "1" : "0");
        if (request.Kind == CredentialLifecycleOperationKind.ReconcileRepair)
        {
            Append(builder, request.InterruptedRepairOperationId!.Value);
        }
        return "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    private static bool IsTerminalPhase(CredentialLifecycleMutationPhase? phase) => phase is CredentialLifecycleMutationPhase.Complete or CredentialLifecycleMutationPhase.Rollback or CredentialLifecycleMutationPhase.TombstoneComplete or CredentialLifecycleMutationPhase.TombstoneUncertain or CredentialLifecycleMutationPhase.RepairComplete or CredentialLifecycleMutationPhase.Uncertain or CredentialLifecycleMutationPhase.RepairUncertain or CredentialLifecycleMutationPhase.LocatorUncertain or CredentialLifecycleMutationPhase.RepairReconciledUncertain or CredentialLifecycleMutationPhase.MetadataComplete;

    private static CredentialLifecycleResult FromRegistry(CredentialLifecycleRequest request, CredentialRegistryMutationResult result, CredentialProviderHealthStatus health, string detail)
    {
        var status = result.Status switch
        {
            CredentialRegistryMutationStatus.Conflict => CredentialLifecycleResultStatus.Conflict,
            CredentialRegistryMutationStatus.NotFound => CredentialLifecycleResultStatus.NotFound,
            CredentialRegistryMutationStatus.Invalid => CredentialLifecycleResultStatus.Invalid,
            _ => CredentialLifecycleResultStatus.Unavailable
        };
        return Result(request, status, result.RegistryRevision, health, [], result.Failure?.Code ?? CredentialFailureCode.Unavailable, detail);
    }

    private static CredentialLifecycleResult Result(CredentialLifecycleRequest request, CredentialLifecycleResultStatus status, long? revision, CredentialProviderHealthStatus health, IReadOnlyList<string> activeRuns, CredentialFailureCode? failure, string detail)
    {
        return new CredentialLifecycleResult(status, request.OperationId, request.Kind, request.ReferenceId, revision, health, activeRuns, failure is null ? null : CredentialFailure.FromCode(failure.Value), detail);
    }

    private async Task<CredentialLifecyclePreview> AuditPreviewAsync(CredentialLifecyclePreview preview, string target)
    {
        var outcome = preview.Status is CredentialLifecyclePreviewStatus.Ready or CredentialLifecyclePreviewStatus.Replayed ? AuditSchema.Outcomes.Succeeded : preview.Status == CredentialLifecyclePreviewStatus.Conflict ? AuditSchema.Outcomes.Conflict : preview.Status == CredentialLifecyclePreviewStatus.Denied ? AuditSchema.Outcomes.Denied : AuditSchema.Outcomes.Failed;
        var metadata = new Dictionary<string, object?> { ["operationId"] = preview.OperationId?.Value, ["transition"] = preview.Kind.ToString(), ["workspaceId"] = IsSafe(preview.WorkspaceId, 256) ? preview.WorkspaceId : "invalid", ["actorId"] = IsSafe(preview.ActorId, 128) ? preview.ActorId : "invalid", ["registryRevision"] = preview.RegistryRevision, ["dependentSetRevision"] = preview.DependentSetRevision, ["previewRevision"] = preview.PreviewRevision, ["impactCount"] = preview.Impacts.Count };
        try
        {
            await _auditLog.AppendAsync(AuditEvent.Create(AuditSchema.Actors.CredentialHost, AuditSchema.Actions.CredentialLifecyclePreview, target, outcome, preview.Detail, metadata), CancellationToken.None);
            return preview;
        }
        catch (Exception)
        {
            return new CredentialLifecyclePreview(CredentialLifecyclePreviewStatus.Unavailable, preview.OperationId!, preview.Kind, preview.ReferenceId!, preview.WorkspaceId, preview.ActorId, null, string.Empty, string.Empty, [], "The credential lifecycle preview audit is unavailable.");
        }
    }

    private async Task<CredentialLifecycleAuditDrainResult> DrainAuditUnderAuthorityAsync(CancellationToken cancellationToken)
    {
        var read = await _registry.ReadAsync(cancellationToken);
        if (!read.Succeeded)
        {
            return new CredentialLifecycleAuditDrainResult(0, 0, CredentialFailure.FromCode(CredentialFailureCode.Unavailable));
        }
        var delivered = 0;
        foreach (var pending in read.PendingAudits)
        {
            var metadata = new Dictionary<string, object?> { ["operationId"] = pending.LifecycleIntentOperationId.Value, ["auditOperationId"] = pending.AuditOperationId.Value, ["transition"] = pending.Kind.ToString(), ["workspaceId"] = pending.WorkspaceId, ["actorId"] = pending.ActorId, ["registryRevision"] = pending.RegistryRevision, ["previewRevision"] = pending.PreviewRevision, ["delivery"] = "credential-registry-outbox-v1" };
            if (string.Equals(pending.Action, AuditSchema.Actions.CredentialLifecycleOutcome, StringComparison.Ordinal))
            {
                metadata["terminalOperationId"] = pending.AuditOperationId.Value;
            }
            var auditEvent = new AuditEvent(pending.OccurredAtUtc, AuditSchema.Actors.CredentialHost, pending.Action, pending.ReferenceId.Value, pending.Outcome, pending.Detail, metadata);
            try
            {
                await _auditLog.AppendAsync(auditEvent, cancellationToken);
            }
            catch (Exception)
            {
                return new CredentialLifecycleAuditDrainResult(delivered, read.PendingAudits.Count - delivered, CredentialFailure.FromCode(CredentialFailureCode.Unavailable));
            }
            if (!await _registry.AcknowledgeAuditAsync(pending.AuditOperationId, CancellationToken.None))
            {
                return new CredentialLifecycleAuditDrainResult(delivered, read.PendingAudits.Count - delivered, CredentialFailure.FromCode(CredentialFailureCode.Unavailable));
            }
            delivered++;
        }
        return new CredentialLifecycleAuditDrainResult(delivered, 0, null);
    }

    private async Task TryAppendAuditAsync(string action, CredentialLifecycleRequest request, string outcome, long? registryRevision, string? previewRevision, string detail)
    {
        var metadata = new Dictionary<string, object?> { ["operationId"] = request.OperationId.Value, ["transition"] = request.Kind.ToString(), ["workspaceId"] = request.WorkspaceId, ["actorId"] = request.ActorId, ["registryRevision"] = registryRevision, ["previewRevision"] = previewRevision, ["confirmed"] = request.Confirmed };
        try
        {
            await _auditLog.AppendAsync(AuditEvent.Create(AuditSchema.Actors.CredentialHost, action, request.ReferenceId.Value, outcome, detail, metadata), CancellationToken.None);
        }
        catch (Exception)
        {
        }
    }

    private static string AuditOutcome(CredentialLifecycleResultStatus status) => status switch
    {
        CredentialLifecycleResultStatus.Applied or CredentialLifecycleResultStatus.Replayed => AuditSchema.Outcomes.Succeeded,
        CredentialLifecycleResultStatus.Conflict => AuditSchema.Outcomes.Conflict,
        CredentialLifecycleResultStatus.Denied => AuditSchema.Outcomes.Denied,
        _ => AuditSchema.Outcomes.Failed
    };
}
