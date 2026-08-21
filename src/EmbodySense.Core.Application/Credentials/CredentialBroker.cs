using EmbodySense.Core.Application.Credentials.Leases;
using EmbodySense.Core.Application.Credentials.Leases.Models;
using EmbodySense.Core.Application.Credentials.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Credentials;
using EmbodySense.Core.Common.Credentials.Leases;
using EmbodySense.Core.Common.Credentials.Leases.Models;
using EmbodySense.Core.Common.Credentials.Models;
using EmbodySense.Core.Common.Secrets.Redaction.Models;

namespace EmbodySense.Core.Application.Credentials;

/// <summary>Authorizes and immediately redeems one exact short-lived credential lease through a callback-only provider boundary.</summary>
public sealed class CredentialBroker : ICredentialBroker
{
    private readonly ICredentialAuthorityProofVerifier _authorityProofVerifier;
    private readonly ICredentialLeaseCurrentAuthorityVerifier _currentAuthorityVerifier;
    private readonly ICredentialLeaseAttemptStore _attemptStore;
    private readonly ICredentialLeaseRedemptionGate _redemptionGate;
    private readonly ICredentialRegistryStore _registry;
    private readonly ICredentialUseEvidenceSink _evidenceSink;
    private readonly ICredentialValueProviderResolver _providerResolver;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates the canonical broker over server-owned value-free verification and persistence ports.</summary>
    public CredentialBroker(
        ICredentialAuthorityProofVerifier authorityProofVerifier,
        ICredentialLeaseCurrentAuthorityVerifier currentAuthorityVerifier,
        ICredentialLeaseAttemptStore attemptStore,
        ICredentialLeaseRedemptionGate redemptionGate,
        ICredentialRegistryStore registry,
        ICredentialUseEvidenceSink evidenceSink,
        ICredentialValueProviderResolver providerResolver,
        TimeProvider? timeProvider = null)
    {
        _authorityProofVerifier = authorityProofVerifier ?? throw new ArgumentNullException(nameof(authorityProofVerifier));
        _currentAuthorityVerifier = currentAuthorityVerifier ?? throw new ArgumentNullException(nameof(currentAuthorityVerifier));
        _attemptStore = attemptStore ?? throw new ArgumentNullException(nameof(attemptStore));
        _redemptionGate = redemptionGate ?? throw new ArgumentNullException(nameof(redemptionGate));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _evidenceSink = evidenceSink ?? throw new ArgumentNullException(nameof(evidenceSink));
        _providerResolver = providerResolver ?? throw new ArgumentNullException(nameof(providerResolver));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async ValueTask<CredentialUseResult> UseAsync(CredentialUseRequest request, CredentialContractId currentRunId, ICredentialTrustedUseConsumer trustedConsumer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(trustedConsumer);
        if (!TryGetTrustedNow(out var now))
        {
            return Failed(CredentialFailureCode.Unavailable);
        }
        if (!ValidateRequest(request, currentRunId, now, out var intent))
        {
            return Failed(CredentialFailureCode.InvalidRequest);
        }

        CredentialLeaseAttemptVersion prepared;
        try
        {
            prepared = CredentialLeaseContract.Prepare(intent!, now);
        }
        catch (ArgumentException)
        {
            return Failed(CredentialFailureCode.InvalidRequest);
        }

        CredentialLeaseAttemptStoreResult begun;
        try
        {
            begun = await _attemptStore.BeginAsync(intent!, prepared, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failed(CredentialFailureCode.Unavailable);
        }
        catch (Exception)
        {
            return Failed(CredentialFailureCode.Unavailable);
        }

        if (begun.Status == CredentialLeaseAttemptStoreStatus.OperationInProgress)
        {
            return Failed(CredentialFailureCode.Conflict, begun.History);
        }
        if (begun.Status is not (CredentialLeaseAttemptStoreStatus.Created or CredentialLeaseAttemptStoreStatus.Replayed)
            || begun.History is null)
        {
            return Failed(StoreFailure(begun.Status), begun.History);
        }
        if (IsTerminal(begun.History.Current.Phase))
        {
            return await ReplayTerminalAsync(begun.History).ConfigureAwait(false);
        }
        if (begun.Lease is null)
        {
            return Failed(CredentialFailureCode.Conflict, begun.History);
        }

        using var owner = begun.Lease;
        if (begun.Status == CredentialLeaseAttemptStoreStatus.Replayed)
        {
            return await RecoverAbandonedAsync(request, begun.History, owner).ConfigureAwait(false);
        }

        var history = begun.History;
        var registryMatch = await ReadRegistryAsync(intent!, now, cancellationToken).ConfigureAwait(false);
        if (!registryMatch.Succeeded)
        {
            return await CloseNotRedeemedAsync(request, history, owner, registryMatch.Failure!.Code, now).ConfigureAwait(false);
        }

        CredentialAuthorityVerificationResult proof;
        try
        {
            proof = await _authorityProofVerifier.VerifyAsync(request, currentRunId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return await CloseNotRedeemedAsync(request, history, owner, CredentialFailureCode.Unavailable, now).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return await CloseNotRedeemedAsync(request, history, owner, CredentialFailureCode.Unavailable, now).ConfigureAwait(false);
        }
        if (proof is null || !proof.Accepted || proof.Failure is not null)
        {
            var failure = proof?.Failure is { } closed && CredentialPortContractValidator.IsFailureValid(closed) ? closed.Code : CredentialFailureCode.Unauthorized;
            return await CloseNotRedeemedAsync(request, history, owner, failure, now).ConfigureAwait(false);
        }

        CredentialValueProviderResolution providerResolution;
        CredentialProviderId providerId;
        if (!CredentialProviderId.TryParse(intent!.Registry.ProviderId, out var parsedProviderId, out _))
        {
            return await CloseNotRedeemedAsync(request, history, owner, CredentialFailureCode.InvalidRequest, now).ConfigureAwait(false);
        }
        providerId = parsedProviderId!;
        try
        {
            providerResolution = await _providerResolver.ResolveAsync(intent.Execution.WorkspaceId, request.Binding.ReferenceId, providerId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return await CloseNotRedeemedAsync(request, history, owner, CredentialFailureCode.Unavailable, now).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return await CloseNotRedeemedAsync(request, history, owner, CredentialFailureCode.Unavailable, now).ConfigureAwait(false);
        }
        if (providerResolution is null
            || providerResolution.Status != CredentialValueProviderResolutionStatus.Resolved
            || providerResolution.Provider is null
            || providerResolution.ProviderId is null
            || !providerResolution.ProviderId.Equals(providerId))
        {
            return await CloseNotRedeemedAsync(request, history, owner, CredentialFailureCode.Unavailable, now).ConfigureAwait(false);
        }

        var providerRequest = new CredentialProviderUseRequest(intent.Execution.WorkspaceId, request.Binding.ReferenceId, providerId, ParseId(intent.CredentialUseOperationId));
        CredentialProviderHealthResult providerHealth;
        try
        {
            providerHealth = await providerResolution.Provider.GetHealthAsync(providerRequest, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return await CloseNotRedeemedAsync(request, history, owner, CredentialFailureCode.Unavailable, now).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return await CloseNotRedeemedAsync(request, history, owner, CredentialFailureCode.Unavailable, now).ConfigureAwait(false);
        }
        if (providerHealth is null || providerHealth.Status != CredentialProviderHealthStatus.Available || providerHealth.Failure is not null)
        {
            return await CloseNotRedeemedAsync(request, history, owner, CredentialFailureCode.Unavailable, now).ConfigureAwait(false);
        }

        if (!TryGetTrustedNow(out now) || now < history.Current.RecordedAtUtc)
        {
            return await CloseNotRedeemedAsync(request, history, owner, CredentialFailureCode.Unavailable, history.Current.RecordedAtUtc).ConfigureAwait(false);
        }
        if (now >= intent.EffectiveExpiresAtUtc)
        {
            return await CloseNotRedeemedAsync(request, history, owner, CredentialFailureCode.Expired, now).ConfigureAwait(false);
        }

        CredentialLeaseCurrentVerificationResult current;
        try
        {
            current = await _currentAuthorityVerifier.VerifyAsync(intent, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return await CloseNotRedeemedAsync(request, history, owner, CredentialFailureCode.Unavailable, now).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return await CloseNotRedeemedAsync(request, history, owner, CredentialFailureCode.Unavailable, now).ConfigureAwait(false);
        }
        if (!MatchesCurrent(intent, current))
        {
            var failure = current?.Status == CredentialLeaseCurrentVerificationStatus.Denied ? CredentialFailureCode.Unauthorized : CredentialFailureCode.Unavailable;
            return await CloseNotRedeemedAsync(request, history, owner, failure, now).ConfigureAwait(false);
        }

        var authorizedVersion = CredentialLeaseContract.Advance(intent, history.Current, CredentialLeasePhase.Authorized, now, current.EvidenceHash, registryMatch.EvidenceHash);
        var authorizedHistory = CredentialLeaseContract.CreateHistory(intent, [.. history.Versions, authorizedVersion]);
        var authorizedCommit = await CompareExchangeAsync(history, authorizedHistory, owner, cancellationToken).ConfigureAwait(false);
        if (authorizedCommit.Status is not (CredentialLeaseAttemptStoreStatus.Created or CredentialLeaseAttemptStoreStatus.Replayed) || authorizedCommit.History is null)
        {
            return Failed(StoreFailure(authorizedCommit.Status), authorizedCommit.History ?? history);
        }
        history = authorizedCommit.History;

        var reservation = await ReserveEvidenceAsync(intent, cancellationToken).ConfigureAwait(false);
        if (!reservation.Succeeded)
        {
            return await CloseNotRedeemedAsync(request, history, owner, reservation.Failure?.Code ?? CredentialFailureCode.Unavailable, now).ConfigureAwait(false);
        }

        CredentialLeaseBoundaryResult boundary;
        if (!TryGetTrustedNow(out now))
        {
            return await CloseNotRedeemedAsync(request, history, owner, CredentialFailureCode.Unavailable, history.Current.RecordedAtUtc).ConfigureAwait(false);
        }
        try
        {
            boundary = await _redemptionGate.TryEnterAsync(history, owner, now, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return await RecoverBoundaryFailureAsync(request, history, owner, now).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return await RecoverBoundaryFailureAsync(request, history, owner, now).ConfigureAwait(false);
        }
        if (boundary.Status != CredentialLeaseBoundaryStatus.Entered || boundary.History?.Current.Phase != CredentialLeasePhase.RedemptionBoundaryReached)
        {
            if (boundary.History is { Current.Phase: CredentialLeasePhase.NotRedeemed })
            {
                return await ReplayTerminalAsync(boundary.History).ConfigureAwait(false);
            }
            return await RecoverBoundaryFailureAsync(request, boundary.History ?? history, owner, now).ConfigureAwait(false);
        }
        history = boundary.History;

        var trackingConsumer = new SingleUseCredentialTrustedUseConsumer(trustedConsumer);
        CredentialProviderResult? providerResult = null;
        Exception? providerException = null;
        try
        {
            providerResult = await providerResolution.Provider.UseAsync(providerRequest, trackingConsumer, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            providerException = exception;
        }
        finally
        {
            trackingConsumer.Close();
        }

        var hasTerminalTime = TryGetTrustedNow(out now) && now >= history.Current.RecordedAtUtc;
        if (!hasTerminalTime)
        {
            now = history.Current.RecordedAtUtc;
        }
        var conclusiveSuccess = hasTerminalTime
            && providerException is null
            && providerResult is not null
            && CredentialPortContractValidator.Validate(providerResult).IsValid
            && providerResult.Succeeded
            && trackingConsumer.InvocationCount == 1
            && trackingConsumer.InvocationCompletedSuccessfully;
        var conclusiveFailure = hasTerminalTime
            && providerException is null
            && providerResult is not null
            && CredentialPortContractValidator.Validate(providerResult).IsValid
            && !providerResult.Succeeded
            && trackingConsumer.InvocationCount == 0;
        var terminalPhase = conclusiveSuccess ? CredentialLeasePhase.Redeemed : conclusiveFailure ? CredentialLeasePhase.RedemptionFailed : CredentialLeasePhase.RedemptionAmbiguous;
        CredentialFailureCode? terminalFailure = conclusiveSuccess ? null : conclusiveFailure ? providerResult!.Failure!.Code : CredentialFailureCode.OutcomeUncertain;
        var terminalVersion = CredentialLeaseContract.Advance(intent, history.Current, terminalPhase, now, failureCode: terminalFailure);
        var terminalHistory = CredentialLeaseContract.CreateHistory(intent, [.. history.Versions, terminalVersion]);
        var terminalCommit = await CompareExchangeAsync(history, terminalHistory, owner, CancellationToken.None).ConfigureAwait(false);
        if (terminalCommit.Status is not (CredentialLeaseAttemptStoreStatus.Created or CredentialLeaseAttemptStoreStatus.Replayed) || terminalCommit.History is null)
        {
            return Failed(CredentialFailureCode.OutcomeUncertain, terminalCommit.History ?? history);
        }

        return await ReplayTerminalAsync(terminalCommit.History).ConfigureAwait(false);
    }

    private async Task<CredentialUseResult> RecoverAbandonedAsync(CredentialUseRequest request, CredentialLeaseAttemptHistory history, ICredentialLeaseAttemptLease owner)
    {
        var now = TryGetTrustedNow(out var trustedNow) ? trustedNow : history.Current.RecordedAtUtc;
        if (history.Current.Phase == CredentialLeasePhase.Authorized)
        {
            var reservation = await ReserveEvidenceAsync(history.Intent, CancellationToken.None).ConfigureAwait(false);
            if (!reservation.Succeeded)
            {
                return Failed(reservation.Failure?.Code ?? CredentialFailureCode.Unavailable, history);
            }
        }
        var phase = history.Current.Phase == CredentialLeasePhase.RedemptionBoundaryReached ? CredentialLeasePhase.RedemptionAmbiguous : CredentialLeasePhase.NotRedeemed;
        var failure = phase == CredentialLeasePhase.RedemptionAmbiguous ? CredentialFailureCode.OutcomeUncertain : CredentialFailureCode.Unavailable;
        var next = CredentialLeaseContract.Advance(history.Intent, history.Current, phase, now < history.Current.RecordedAtUtc ? history.Current.RecordedAtUtc : now, failureCode: failure);
        var replacement = CredentialLeaseContract.CreateHistory(history.Intent, [.. history.Versions, next]);
        var result = await CompareExchangeAsync(history, replacement, owner, CancellationToken.None).ConfigureAwait(false);
        return result.History is not null && result.Status is CredentialLeaseAttemptStoreStatus.Created or CredentialLeaseAttemptStoreStatus.Replayed
            ? await ReplayTerminalAsync(result.History).ConfigureAwait(false)
            : Failed(phase == CredentialLeasePhase.RedemptionAmbiguous ? CredentialFailureCode.OutcomeUncertain : StoreFailure(result.Status), result.History ?? history);
    }

    private async Task<CredentialUseResult> CloseNotRedeemedAsync(CredentialUseRequest request, CredentialLeaseAttemptHistory history, ICredentialLeaseAttemptLease owner, CredentialFailureCode failure, DateTimeOffset now)
    {
        if (history.Current.Phase == CredentialLeasePhase.RedemptionBoundaryReached)
        {
            return Failed(CredentialFailureCode.OutcomeUncertain, history);
        }
        var recorded = now < history.Current.RecordedAtUtc ? history.Current.RecordedAtUtc : now;
        var next = CredentialLeaseContract.Advance(history.Intent, history.Current, CredentialLeasePhase.NotRedeemed, recorded, failureCode: failure);
        var replacement = CredentialLeaseContract.CreateHistory(history.Intent, [.. history.Versions, next]);
        var result = await CompareExchangeAsync(history, replacement, owner, CancellationToken.None).ConfigureAwait(false);
        return result.History is not null && result.Status is CredentialLeaseAttemptStoreStatus.Created or CredentialLeaseAttemptStoreStatus.Replayed
            ? await ReplayTerminalAsync(result.History).ConfigureAwait(false)
            : Failed(StoreFailure(result.Status), result.History ?? history);
    }

    private async Task<CredentialUseResult> RecoverBoundaryFailureAsync(CredentialUseRequest request, CredentialLeaseAttemptHistory authorized, ICredentialLeaseAttemptLease owner, DateTimeOffset now)
    {
        CredentialLeaseAttemptStoreResult observed;
        try
        {
            observed = await _attemptStore.ReadAsync(authorized.Intent.CredentialUseOperationId, authorized.Intent.CredentialUseGeneration, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return Failed(CredentialFailureCode.OutcomeUncertain, authorized);
        }

        if (observed.History is not { } durable
            || observed.Status is not CredentialLeaseAttemptStoreStatus.Replayed)
        {
            return Failed(CredentialFailureCode.OutcomeUncertain, observed.History ?? authorized);
        }
        if (durable.Current.Phase == CredentialLeasePhase.Authorized)
        {
            return await CloseNotRedeemedAsync(request, durable, owner, CredentialFailureCode.Unavailable, now).ConfigureAwait(false);
        }
        if (durable.Current.Phase == CredentialLeasePhase.RedemptionBoundaryReached)
        {
            var recorded = now < durable.Current.RecordedAtUtc ? durable.Current.RecordedAtUtc : now;
            var ambiguous = CredentialLeaseContract.Advance(durable.Intent, durable.Current, CredentialLeasePhase.RedemptionAmbiguous, recorded, failureCode: CredentialFailureCode.OutcomeUncertain);
            var replacement = CredentialLeaseContract.CreateHistory(durable.Intent, [.. durable.Versions, ambiguous]);
            var committed = await CompareExchangeAsync(durable, replacement, owner, CancellationToken.None).ConfigureAwait(false);
            return committed.History is not null && committed.Status is CredentialLeaseAttemptStoreStatus.Created or CredentialLeaseAttemptStoreStatus.Replayed
                ? await ReplayTerminalAsync(committed.History).ConfigureAwait(false)
                : Failed(CredentialFailureCode.OutcomeUncertain, committed.History ?? durable);
        }
        return IsTerminal(durable.Current.Phase)
            ? await ReplayTerminalAsync(durable).ConfigureAwait(false)
            : Failed(CredentialFailureCode.OutcomeUncertain, durable);
    }

    private async Task<CredentialLeaseAttemptStoreResult> CompareExchangeAsync(CredentialLeaseAttemptHistory current, CredentialLeaseAttemptHistory replacement, ICredentialLeaseAttemptLease owner, CancellationToken cancellationToken)
    {
        try
        {
            return await _attemptStore.CompareExchangeAsync(current.Current.ContentHash, replacement, owner, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new CredentialLeaseAttemptStoreResult(CredentialLeaseAttemptStoreStatus.Unavailable, current);
        }
        catch (Exception)
        {
            return new CredentialLeaseAttemptStoreResult(CredentialLeaseAttemptStoreStatus.Unavailable, current);
        }
    }

    private async Task<CredentialLeaseRegistryMatch> ReadRegistryAsync(CredentialLeaseIntent intent, DateTimeOffset now, CancellationToken cancellationToken)
    {
        try
        {
            return CredentialLeaseRegistryMatcher.Match(intent, await _registry.ReadAsync(cancellationToken).ConfigureAwait(false), now);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return CredentialLeaseRegistryMatch.Rejected(CredentialFailureCode.Unavailable);
        }
        catch (Exception)
        {
            return CredentialLeaseRegistryMatch.Rejected(CredentialFailureCode.Unavailable);
        }
    }

    private async Task<CredentialUseResult> ReplayTerminalAsync(CredentialLeaseAttemptHistory history)
    {
        var current = history.Current;
        var outcome = current.Phase switch
        {
            CredentialLeasePhase.Redeemed => CredentialUseOutcome.Succeeded,
            CredentialLeasePhase.RedemptionAmbiguous => CredentialUseOutcome.OutcomeUncertain,
            _ => CredentialUseOutcome.FailedBeforeActuation,
        };
        var evidence = new CredentialUseEvidence(
            CredentialUseEvidence.CurrentSchemaVersion,
            CredentialLeaseContract.ComputeEvidenceId(history.Intent.CredentialUseOperationId, history.Intent.CredentialUseGeneration),
            ParseReferenceId(history.Intent.Registry.ReferenceId),
            ParseCredentialHash(history.Intent.Registry.BindingHash),
            ParseId(history.Intent.Authority.AuthorityProofId),
            ParseId(history.Intent.Execution.RunId),
            BuildSafeScope(history.Intent),
            current.RecordedAtUtc,
            outcome,
            true,
            new CredentialLeaseUseEvidence(
                CredentialLeaseUseEvidence.CurrentSchemaVersion,
                history,
                new RedactionSummary(RedactionStatus.Completed, 0, 0, 0, 0, 0)));
        CredentialEvidenceWriteResult write;
        try
        {
            write = await _evidenceSink.AppendAsync(evidence, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            write = CredentialEvidenceWriteResult.Failed(CredentialFailure.FromCode(CredentialFailureCode.Unavailable));
        }
        if (!write.Succeeded)
        {
            return Failed(write.Failure?.Code ?? CredentialFailureCode.Unavailable, history);
        }
        return current.Phase == CredentialLeasePhase.Redeemed
            ? CredentialUseResult.Success(evidence, history)
            : Failed(current.FailureCode ?? CredentialFailureCode.Unavailable, history);
    }

    private async ValueTask<CredentialEvidenceWriteResult> ReserveEvidenceAsync(CredentialLeaseIntent intent, CancellationToken cancellationToken)
    {
        try
        {
            return await _evidenceSink.ReserveAsync(intent, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return CredentialEvidenceWriteResult.Failed(CredentialFailure.FromCode(CredentialFailureCode.Unavailable));
        }
        catch (Exception)
        {
            return CredentialEvidenceWriteResult.Failed(CredentialFailure.FromCode(CredentialFailureCode.Unavailable));
        }
    }

    private static bool ValidateRequest(CredentialUseRequest? request, CredentialContractId? currentRunId, DateTimeOffset now, out CredentialLeaseIntent? intent)
    {
        intent = request?.LeaseIntent;
        if (request is null
            || currentRunId is null
            || intent is null
            || CredentialLeaseContract.Validate(intent) is not null
            || !CredentialContractValidator.Validate(request, currentRunId, now).IsValid
            || !CredentialContractJson.TryHash(request.AuthorityProof, out var authorityProofHash, out _))
        {
            return false;
        }

        var scope = request.RequestedScope;
        return string.Equals(intent.Execution.WorkspaceId, scope.WorkspaceId, StringComparison.Ordinal)
            && string.Equals(intent.Execution.ActorId, scope.ActorId, StringComparison.Ordinal)
            && string.Equals(intent.Execution.ActorId, request.AuthorityProof.ActorId, StringComparison.Ordinal)
            && string.Equals(intent.Authority.AuthorityProofId, request.AuthorityProof.ProofId.Value, StringComparison.Ordinal)
            && string.Equals(intent.Authority.AuthorityProofHash, authorityProofHash!.Value, StringComparison.Ordinal)
            && string.Equals(intent.Execution.RunId, currentRunId.Value, StringComparison.Ordinal)
            && string.Equals(intent.Execution.RunId, request.AuthorityProof.RunId.Value, StringComparison.Ordinal)
            && string.Equals(intent.Execution.RoleId, scope.RoleId, StringComparison.Ordinal)
            && string.Equals(intent.Execution.LoopId, scope.LoopId, StringComparison.Ordinal)
            && scope.LoopRevision == intent.Execution.DeclaredLoopRevision
            && string.Equals(intent.Effect.NodeId, scope.NodeId, StringComparison.Ordinal)
            && scope.Service is not null
            && scope.Target is not null
            && string.Equals(intent.Target.TargetClass, scope.Service, StringComparison.Ordinal)
            && string.Equals(intent.Target.TargetFingerprint, CredentialLeaseContract.ComputeTargetFingerprint(scope.Service, System.Text.Encoding.UTF8.GetBytes(scope.Target)), StringComparison.Ordinal)
            && string.Equals(intent.Target.OperationClass, scope.OperationClass, StringComparison.Ordinal)
            && request.Purpose is not null
            && string.Equals(intent.Target.Purpose, request.Purpose, StringComparison.Ordinal)
            && string.Equals(intent.Registry.ReferenceId, request.Binding.ReferenceId.Value, StringComparison.Ordinal)
            && string.Equals(intent.Registry.ReferenceId, request.AuthorityProof.ReferenceId.Value, StringComparison.Ordinal)
            && string.Equals(intent.Registry.BindingHash, request.BindingHash.Value, StringComparison.Ordinal)
            && string.Equals(intent.Registry.BindingHash, request.AuthorityProof.BindingHash.Value, StringComparison.Ordinal)
            && string.Equals(intent.Capability.CapabilityId, request.Binding.Capability.Id.Value, StringComparison.Ordinal)
            && string.Equals(intent.Capability.CapabilityVersion, request.Binding.Capability.Version.Value, StringComparison.Ordinal)
            && string.Equals(intent.Capability.CapabilityDescriptorHash, request.Binding.Capability.Hash.Value, StringComparison.Ordinal)
            && string.Equals(intent.Capability.CapabilityProviderId, request.Binding.Implementation.ProviderId.Value, StringComparison.Ordinal)
            && string.Equals(intent.Capability.CapabilityImplementationId, request.Binding.Implementation.ImplementationId, StringComparison.Ordinal)
            && string.Equals(intent.Capability.SecretRequirement, request.Binding.Requirement.Name, StringComparison.Ordinal)
            && now >= intent.IssuedAtUtc
            && now < intent.EffectiveExpiresAtUtc;
    }

    private static bool MatchesCurrent(CredentialLeaseIntent intent, CredentialLeaseCurrentVerificationResult? result)
        => result is not null
            && result.Status == CredentialLeaseCurrentVerificationStatus.Authorized
            && string.Equals(result.VerifiedIntentHash, intent.ContentHash, StringComparison.Ordinal)
            && IsHash(result.EvidenceHash)
            && string.Equals(result.CurrentAuthorityDecisionHash, intent.Authority.CurrentAuthorityDecisionHash, StringComparison.Ordinal)
            && string.Equals(result.CapabilityDescriptorHash, intent.Capability.CapabilityDescriptorHash, StringComparison.Ordinal)
            && (intent.Profile.Applicability == CredentialLeaseProfileApplicability.NotApplicable && result.ProfileHash is null
                || intent.Profile.Applicability == CredentialLeaseProfileApplicability.Applicable && string.Equals(result.ProfileHash, intent.Profile.ProfileHash, StringComparison.Ordinal));

    private static bool IsTerminal(CredentialLeasePhase phase) => phase is CredentialLeasePhase.NotRedeemed or CredentialLeasePhase.Redeemed or CredentialLeasePhase.RedemptionFailed or CredentialLeasePhase.RedemptionAmbiguous;

    private static CredentialFailureCode StoreFailure(CredentialLeaseAttemptStoreStatus status) => status switch
    {
        CredentialLeaseAttemptStoreStatus.Conflict or CredentialLeaseAttemptStoreStatus.OperationInProgress => CredentialFailureCode.Conflict,
        CredentialLeaseAttemptStoreStatus.Backpressured => CredentialFailureCode.LimitExceeded,
        CredentialLeaseAttemptStoreStatus.Corrupt => CredentialFailureCode.OutcomeUncertain,
        _ => CredentialFailureCode.Unavailable,
    };

    private static CredentialUseResult Failed(CredentialFailureCode code, CredentialLeaseAttemptHistory? history = null) => CredentialUseResult.Failed(CredentialFailure.FromCode(code), history);

    private static CredentialContractId ParseId(string value)
        => CredentialContractId.TryParse(value, out var id, out _) ? id! : throw new InvalidOperationException("A validated lease contract identity became invalid.");

    private static CredentialReferenceId ParseReferenceId(string value)
        => CredentialReferenceId.TryParse(value, out var id, out _) ? id! : throw new InvalidOperationException("A validated lease reference identity became invalid.");

    private static CredentialContractHash ParseCredentialHash(string value)
        => CredentialContractHash.TryParse(value, out var hash, out _) ? hash! : throw new InvalidOperationException("A validated lease credential hash became invalid.");

    private static CredentialScope BuildSafeScope(CredentialLeaseIntent intent)
    {
        if (!CapabilityId.TryParse(intent.Capability.CapabilityId, out var capabilityId, out _)
            || !CapabilityVersion.TryParse(intent.Capability.CapabilityVersion, out var capabilityVersion, out _)
            || !CapabilityDescriptorHash.TryParse(intent.Capability.CapabilityDescriptorHash, out var capabilityHash, out _)
            || !CapabilityProviderId.TryParse(intent.Capability.CapabilityProviderId, out var providerId, out _))
        {
            throw new InvalidOperationException("A validated lease capability identity became invalid.");
        }

        return new CredentialScope(
            intent.Execution.WorkspaceId,
            intent.Execution.RoleId,
            intent.Execution.LoopId,
            intent.Execution.DeclaredLoopRevision,
            intent.Effect.NodeId,
            new CapabilityDescriptorIdentity(capabilityId!, capabilityVersion!, capabilityHash!),
            new CapabilityImplementationIdentity(providerId!, intent.Capability.CapabilityImplementationId),
            intent.Target.TargetClass,
            null,
            intent.Target.OperationClass,
            intent.Execution.ActorId,
            intent.IssuedAtUtc,
            intent.EffectiveExpiresAtUtc);
    }

    private bool TryGetTrustedNow(out DateTimeOffset now)
    {
        try
        {
            now = _timeProvider.GetUtcNow();
            return now != default && now.Offset == TimeSpan.Zero;
        }
        catch (Exception)
        {
            now = default;
            return false;
        }
    }

    private static bool IsHash(string? value)
        => value is { Length: 71 }
            && value.StartsWith("sha256:", StringComparison.Ordinal)
            && value[7..].All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
