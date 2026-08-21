using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Application.Inference.Profiles.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Inference;
using EmbodySense.Core.Common.Inference.Profiles;
using EmbodySense.Core.Common.Inference.Profiles.Models;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Admission;

namespace EmbodySense.Core.Application.Inference.Profiles;

/// <summary>Revalidates current exact primary eligibility and atomically reserves provider usage before transport.</summary>
/// <remarks>No path in this service selects or executes an admitted fallback.</remarks>
public sealed class GovernedModelAttemptAdmissionService
{
    private const int MaximumCatalogPageSize = 50;
    private const int MaximumCatalogEntries = 512;
    private readonly ICapabilityCatalogStore _capabilityCatalog;
    private readonly IModelProfileMetadataSource _metadataSource;
    private readonly IModelProfileAdapterRegistry _adapterRegistry;
    private readonly IModelInferenceDataPostureSource _dataPostureSource;
    private readonly IModelAttemptAuthorityRevalidator _authorityRevalidator;
    private readonly IGovernedModelUsageLedger _ledger;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates the current attempt eligibility and reservation service.</summary>
    public GovernedModelAttemptAdmissionService(ICapabilityCatalogStore capabilityCatalog, IModelProfileMetadataSource metadataSource, IModelProfileAdapterRegistry adapterRegistry, IModelInferenceDataPostureSource dataPostureSource, IModelAttemptAuthorityRevalidator authorityRevalidator, IGovernedModelUsageLedger ledger, TimeProvider? timeProvider = null)
    {
        _capabilityCatalog = capabilityCatalog ?? throw new ArgumentNullException(nameof(capabilityCatalog));
        _metadataSource = metadataSource ?? throw new ArgumentNullException(nameof(metadataSource));
        _adapterRegistry = adapterRegistry ?? throw new ArgumentNullException(nameof(adapterRegistry));
        _dataPostureSource = dataPostureSource ?? throw new ArgumentNullException(nameof(dataPostureSource));
        _authorityRevalidator = authorityRevalidator ?? throw new ArgumentNullException(nameof(authorityRevalidator));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Returns only after an exact reservation is durable; callers must not cross provider transport before success.</summary>
    public async Task<GovernedModelAttemptAdmissionResult> ReserveAsync(
        GovernedModelAttemptAdmissionRequest? request,
        LlmInferenceRequest? inferenceRequest,
        CancellationToken cancellationToken = default)
    {
        if (!TryValidateRequest(request, out var node)
            || !GovernedModelInferencePayloadHash.TryCompute(inferenceRequest, out var inputPayloadHash))
        {
            return Result(GovernedModelAttemptAdmissionStatus.Invalid);
        }

        var admission = request!.RoutingAdmission;
        var primary = node!.Primary;
        if (!string.Equals(request.RequestedPrimaryPinHash, primary.ContentHash, StringComparison.Ordinal))
        {
            return Result(GovernedModelAttemptAdmissionStatus.Invalid);
        }
        if (!GovernedModelBudgetPolicy.TryRestrictPerAttempt(node.Requirements.Budget, request.RetryUsageCeiling, out var effectiveBudget))
        {
            return Result(GovernedModelAttemptAdmissionStatus.Invalid);
        }

        var priorOperation = await ReadPriorOperationAsync(request, primary, cancellationToken).ConfigureAwait(false);
        if (priorOperation.Status == PriorOperationReadStatus.Unavailable)
        {
            return Result(GovernedModelAttemptAdmissionStatus.Unavailable);
        }

        var current = await RevalidateCurrentAsync(
            request,
            node,
            primary,
            new ModelInferenceDataPostureRequest(request, inferenceRequest!, inputPayloadHash!),
            cancellationToken).ConfigureAwait(false);
        if (priorOperation.Entries is { Count: > 0 } priorEntries
            && !PriorOperationMatchesRequest(priorEntries[0].Identity, request, primary, effectiveBudget!))
        {
            return ConflictingPriorOperationResult(primary, priorEntries);
        }
        if (current.Status != GovernedModelAttemptAdmissionStatus.Reserved || current.Evidence is null)
        {
            if (priorOperation.Entries is { Count: > 0 } retainedEntries)
            {
                return PriorOperationResult(
                    retainedEntries.Count > 1
                        ? GovernedModelAttemptAdmissionStatus.AlreadyAdvanced
                        : current.Status,
                    primary,
                    retainedEntries);
            }
            return Result(current.Status);
        }

        GovernedModelUsageLedgerIdentity identity;
        try
        {
            identity = GovernedModelUsageLedgerIdentity.Create(1, admission.WorkspaceId, request.RunId, admission.GraphId, admission.GraphRevisionId, admission.GraphExecutableHash, request.ExecutionGeneration, request.AdmissionReceipt.ContentHash, admission.ContentHash, current.Evidence.AuthorityEvidenceHash, current.Evidence.DataPostureEvidenceHash, request.NodeId, request.PlanOrdinal, request.ActivationOrdinal, request.VisitOrdinal, request.AttemptOperationId, request.AttemptNumber, primary.ContentHash, effectiveBudget!.ContentHash);
        }
        catch
        {
            return Result(GovernedModelAttemptAdmissionStatus.Invalid);
        }

        if (priorOperation.Entries is { Count: > 0 } exactPriorEntries)
        {
            return string.Equals(exactPriorEntries[0].Identity.ContentHash, identity.ContentHash, StringComparison.Ordinal)
                ? Replay(
                    new GovernedModelUsageLedgerReadResult(
                        GovernedModelUsageLedgerReadStatus.Found,
                        exactPriorEntries,
                        exactPriorEntries.Count),
                    identity,
                    primary)
                : ConflictingPriorOperationResult(primary, exactPriorEntries);
        }

        var prior = await ReadLedgerAsync(identity, cancellationToken).ConfigureAwait(false);
        if (prior is null || prior.Status == GovernedModelUsageLedgerReadStatus.Unavailable)
        {
            return Result(GovernedModelAttemptAdmissionStatus.Unavailable);
        }
        if (prior.Status == GovernedModelUsageLedgerReadStatus.Found)
        {
            return Replay(prior, identity, primary);
        }

        try
        {
            var reserve = await _ledger.ReserveAsync(new GovernedModelUsageReservationRequest(identity, effectiveBudget!, primary.ContentHash, _timeProvider.GetUtcNow()), cancellationToken).ConfigureAwait(false);
            if (!IsValidReservationResult(reserve, identity, primary))
            {
                return Result(GovernedModelAttemptAdmissionStatus.Unavailable);
            }
            if (reserve!.Status == GovernedModelUsageLedgerAppendStatus.Conflict)
            {
                return Result(GovernedModelAttemptAdmissionStatus.Conflict);
            }
            if (reserve.Status == GovernedModelUsageLedgerAppendStatus.BudgetExhausted)
            {
                return Result(GovernedModelAttemptAdmissionStatus.BudgetExhausted);
            }
            if (reserve.Status == GovernedModelUsageLedgerAppendStatus.Unavailable)
            {
                return Result(GovernedModelAttemptAdmissionStatus.Unavailable);
            }
            return await AuthenticateReservationAsync(
                identity,
                reserve.ReservationEntry!,
                primary,
                reserve.Status == GovernedModelUsageLedgerAppendStatus.Appended ? GovernedModelAttemptAdmissionStatus.Reserved : GovernedModelAttemptAdmissionStatus.Replayed).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return await AuthenticateUnknownReservationAsync(identity, primary).ConfigureAwait(false);
        }
        catch
        {
            return await AuthenticateUnknownReservationAsync(identity, primary).ConfigureAwait(false);
        }
    }

    private async Task<GovernedModelAttemptAdmissionResult> AuthenticateUnknownReservationAsync(GovernedModelUsageLedgerIdentity identity, GovernedModelProfilePin primary)
    {
        var retained = await ReadLedgerAsync(identity, CancellationToken.None).ConfigureAwait(false);
        return retained is null ? Result(GovernedModelAttemptAdmissionStatus.Unavailable) : Replay(retained, identity, primary);
    }

    private async Task<GovernedModelAttemptAdmissionResult> AuthenticateReservationAsync(GovernedModelUsageLedgerIdentity identity, GovernedModelUsageLedgerEntry reservation, GovernedModelProfilePin primary, GovernedModelAttemptAdmissionStatus exactSuccessStatus)
    {
        try
        {
            var retained = await ReadLedgerAsync(identity, CancellationToken.None).ConfigureAwait(false);
            if (retained is null)
            {
                return Result(GovernedModelAttemptAdmissionStatus.Unavailable);
            }
            var replay = Replay(retained, identity, primary);
            if (replay.ReservationEntry is null || !string.Equals(replay.ReservationEntry.ContentHash, reservation.ContentHash, StringComparison.Ordinal))
            {
                return Result(GovernedModelAttemptAdmissionStatus.Conflict);
            }
            return replay.Status == GovernedModelAttemptAdmissionStatus.Replayed && exactSuccessStatus == GovernedModelAttemptAdmissionStatus.Reserved
                ? replay with { Status = GovernedModelAttemptAdmissionStatus.Reserved }
                : replay;
        }
        catch
        {
            return Result(GovernedModelAttemptAdmissionStatus.Unavailable);
        }
    }

    private static GovernedModelAttemptAdmissionResult Replay(GovernedModelUsageLedgerReadResult read, GovernedModelUsageLedgerIdentity requested, GovernedModelProfilePin primary)
    {
        if (read.Status != GovernedModelUsageLedgerReadStatus.Found)
        {
            return Result(read.Status == GovernedModelUsageLedgerReadStatus.NotFound ? GovernedModelAttemptAdmissionStatus.Conflict : GovernedModelAttemptAdmissionStatus.Unavailable);
        }

        var first = read.Entries[0];
        if (first.Phase != GovernedModelUsageLedgerPhase.ReservationCommitted
            || !string.Equals(first.Identity.ContentHash, requested.ContentHash, StringComparison.Ordinal)
            || first.Reservation is null
            || !string.Equals(first.EvidenceHash, primary.ContentHash, StringComparison.Ordinal))
        {
            return Result(GovernedModelAttemptAdmissionStatus.Conflict);
        }

        // Any phase after generation one proves transport/reconciliation already advanced. It must never dispatch again.
        return read.Entries.Count == 1
            ? new GovernedModelAttemptAdmissionResult(GovernedModelAttemptAdmissionStatus.Replayed, primary, first, first)
            : new GovernedModelAttemptAdmissionResult(
                GovernedModelAttemptAdmissionStatus.AlreadyAdvanced,
                primary,
                first,
                read.Entries[^1],
                read.Entries.Any(entry => entry.Phase == GovernedModelUsageLedgerPhase.DispatchBoundaryReached));
    }

    private async Task<CurrentAttemptResult> RevalidateCurrentAsync(
        GovernedModelAttemptAdmissionRequest request,
        GovernedModelRoutingAdmissionEntry node,
        GovernedModelProfilePin primary,
        ModelInferenceDataPostureRequest dataRequest,
        CancellationToken cancellationToken)
    {
        try
        {
            var exact = await ReadExactCapabilityAsync(primary.Capability.DescriptorIdentity.Id, cancellationToken).ConfigureAwait(false);
            if (exact.Status == ExactCapabilityStatus.Unavailable)
            {
                return Current(GovernedModelAttemptAdmissionStatus.Unavailable);
            }
            if (exact.Entry is null || !PinMatchesCatalog(primary.Capability, exact.Entry) || !IsReady(exact.Entry.Lifecycle))
            {
                return Current(GovernedModelAttemptAdmissionStatus.Ineligible);
            }

            var metadata = await _metadataSource.ReadAsync(primary.Capability.DescriptorIdentity.Id, cancellationToken).ConfigureAwait(false);
            if (!IsValidMetadataRead(metadata))
            {
                return Current(GovernedModelAttemptAdmissionStatus.Unavailable);
            }
            if (metadata!.Status != ModelProfileSourceReadStatus.Found)
            {
                return Current(metadata.Status == ModelProfileSourceReadStatus.NotFound ? GovernedModelAttemptAdmissionStatus.Ineligible : GovernedModelAttemptAdmissionStatus.Unavailable);
            }
            if (!string.Equals(metadata.Metadata!.ContentHash, primary.Metadata.ContentHash, StringComparison.Ordinal)
                || !string.Equals(metadata.SourceRevisionHash, primary.ProfileSourceRevisionHash, StringComparison.Ordinal))
            {
                return Current(GovernedModelAttemptAdmissionStatus.Ineligible);
            }

            var adapter = await _adapterRegistry.ReadPostureAsync(metadata.Metadata, cancellationToken).ConfigureAwait(false);
            if (!ModelProfileCatalogService.IsAdapterPosture(adapter, metadata.Metadata.ContentHash))
            {
                return Current(GovernedModelAttemptAdmissionStatus.Unavailable);
            }
            if (!string.Equals(adapter!.RegistryRevisionHash, primary.AdapterRegistryRevisionHash, StringComparison.Ordinal)
                || adapter.Status != ModelProfileAdapterPostureStatus.Ready)
            {
                return Current(adapter.Status == ModelProfileAdapterPostureStatus.Unavailable ? GovernedModelAttemptAdmissionStatus.Unavailable : GovernedModelAttemptAdmissionStatus.Ineligible);
            }

            var data = await _dataPostureSource.ReadAsync(dataRequest, cancellationToken).ConfigureAwait(false);
            if (!IsValidDataPosture(data, request, dataRequest.InputPayloadHash))
            {
                return Current(GovernedModelAttemptAdmissionStatus.Unavailable);
            }
            if (data!.Status != ModelInferenceDataPostureStatus.Available)
            {
                return Current(GovernedModelAttemptAdmissionStatus.Unavailable);
            }

            var authority = await _authorityRevalidator.RevalidateAsync(request, node, primary, data, cancellationToken).ConfigureAwait(false);
            if (!IsValidAuthority(authority, request, node, primary))
            {
                return Current(GovernedModelAttemptAdmissionStatus.Unavailable);
            }
            if (authority!.Status != ModelAttemptAuthorityStatus.Allowed)
            {
                return Current(authority.Status == ModelAttemptAuthorityStatus.Denied ? GovernedModelAttemptAdmissionStatus.Ineligible : GovernedModelAttemptAdmissionStatus.Unavailable);
            }

            return node.Requirements.SatisfiedBy(metadata.Metadata, data.DataClasses, request.RoutingAdmission.OwningRoleId, node.NodeTypeId)
                ? new CurrentAttemptResult(GovernedModelAttemptAdmissionStatus.Reserved, new GovernedModelCurrentAttemptEvidence(authority.EvidenceHash!, data.EvidenceHash!))
                : Current(GovernedModelAttemptAdmissionStatus.Ineligible);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Current(GovernedModelAttemptAdmissionStatus.Unavailable);
        }
    }

    private async Task<ExactCapabilityResult> ReadExactCapabilityAsync(CapabilityId profileId, CancellationToken cancellationToken)
    {
        string? cursor = null;
        long? revision = null;
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        CapabilityCatalogEntry? match = null;
        var count = 0;
        do
        {
            CapabilityCatalogReadResult read;
            try
            {
                read = await _capabilityCatalog.ReadAsync(cursor, MaximumCatalogPageSize, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return new ExactCapabilityResult(ExactCapabilityStatus.Unavailable, null);
            }

            if (read is null || read.Status != CapabilityCatalogReadStatus.Available || read.Page is null || read.Page.CatalogRevision < 0 || revision is not null && revision != read.Page.CatalogRevision)
            {
                return new ExactCapabilityResult(ExactCapabilityStatus.Unavailable, null);
            }

            IReadOnlyList<CapabilityCatalogEntry> page;
            try
            {
                page = ModelProfileApplicationContractCopy.Snapshot(read.Page.Entries, MaximumCatalogPageSize, nameof(read.Page.Entries));
            }
            catch
            {
                return new ExactCapabilityResult(ExactCapabilityStatus.Unavailable, null);
            }
            revision = read.Page.CatalogRevision;
            var prior = cursor;
            foreach (var entry in page)
            {
                if (!ModelProfileCatalogService.IsValidCatalogEntry(entry)
                    || prior is not null && string.Compare(entry.Descriptor.Id.Value, prior, StringComparison.Ordinal) <= 0
                    || ++count > MaximumCatalogEntries)
                {
                    return new ExactCapabilityResult(ExactCapabilityStatus.Unavailable, null);
                }
                if (entry.Descriptor.Id.Equals(profileId))
                {
                    if (match is not null)
                    {
                        return new ExactCapabilityResult(ExactCapabilityStatus.Unavailable, null);
                    }
                    match = entry;
                }
                prior = entry.Descriptor.Id.Value;
            }
            var next = read.Page.NextCursor;
            if (next is not null && (page.Count == 0 || !CapabilityId.TryParse(next, out _, out _) || !string.Equals(next, prior, StringComparison.Ordinal) || !seenCursors.Add(next)))
            {
                return new ExactCapabilityResult(ExactCapabilityStatus.Unavailable, null);
            }
            cursor = next;
        }
        while (cursor is not null);

        return new ExactCapabilityResult(ExactCapabilityStatus.Available, match);
    }

    private async Task<GovernedModelUsageLedgerReadResult?> ReadLedgerAsync(GovernedModelUsageLedgerIdentity identity, CancellationToken cancellationToken)
    {
        try
        {
            var read = await _ledger.ReadAsync(identity, cancellationToken).ConfigureAwait(false);
            return ModelUsageLedgerReadAuthentication.TryAuthenticate(read, identity, out var authenticated) ? authenticated : null;
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

    private async Task<PriorOperationReadResult> ReadPriorOperationAsync(
        GovernedModelAttemptAdmissionRequest request,
        GovernedModelProfilePin primary,
        CancellationToken cancellationToken)
    {
        GovernedModelUsageLedgerRunReadResult? read;
        try
        {
            read = await _ledger.ReadRunAsync(
                request.RoutingAdmission.WorkspaceId,
                request.RunId,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return PriorOperationUnavailable();
        }

        if (!TryAuthenticateRunRead(read, request.RoutingAdmission.WorkspaceId, request.RunId, out var entries))
        {
            return PriorOperationUnavailable();
        }
        if (read!.Status == GovernedModelUsageLedgerReadStatus.Unavailable)
        {
            return PriorOperationUnavailable();
        }

        var operationEntries = entries!
            .Where(entry => string.Equals(entry.Identity.AttemptOperationId, request.AttemptOperationId, StringComparison.Ordinal))
            .ToArray();
        if (operationEntries.Length == 0)
        {
            return PriorOperationNotFound();
        }
        if (operationEntries.Length > GovernedModelContractLimits.MaxUsageLedgerEntries
            || !GovernedModelUsageLedgerHistoryValidator.IsValid(
                operationEntries,
                operationEntries[0].Identity,
                operationEntries.Length)
            || !string.Equals(operationEntries[0].EvidenceHash, primary.ContentHash, StringComparison.Ordinal))
        {
            return PriorOperationUnavailable();
        }

        return new PriorOperationReadResult(
            PriorOperationReadStatus.Found,
            Array.AsReadOnly(operationEntries));
    }

    private static bool TryAuthenticateRunRead(
        GovernedModelUsageLedgerRunReadResult? read,
        string workspaceId,
        string runId,
        out IReadOnlyList<GovernedModelUsageLedgerEntry>? entries)
    {
        entries = null;
        if (read is null
            || !Enum.IsDefined(read.Status)
            || read.Entries is null
            || read.WorkspaceGeneration < 0
            || read.Entries.Count > GovernedModelContractLimits.MaxWorkspaceUsageLedgerEntries
            || read.WorkspaceGeneration < read.Entries.Count
            || read.Status == GovernedModelUsageLedgerReadStatus.Found != (read.Entries.Count > 0)
            || read.Status != GovernedModelUsageLedgerReadStatus.Found && read.Entries.Count != 0)
        {
            return false;
        }

        var operationIdentities = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in read.Entries)
        {
            if (!GovernedModelContractValidator.IsValid(entry)
                || !string.Equals(entry.Identity.WorkspaceId, workspaceId, StringComparison.Ordinal)
                || !string.Equals(entry.Identity.RunId, runId, StringComparison.Ordinal)
                || operationIdentities.TryGetValue(entry.Identity.AttemptOperationId, out var identityHash)
                    && !string.Equals(identityHash, entry.Identity.ContentHash, StringComparison.Ordinal))
            {
                return false;
            }
            operationIdentities[entry.Identity.AttemptOperationId] = entry.Identity.ContentHash;
        }

        foreach (var history in read.Entries
            .GroupBy(entry => entry.Identity.ContentHash, StringComparer.Ordinal)
            .Select(group => group.ToArray()))
        {
            if (!GovernedModelUsageLedgerHistoryValidator.IsValid(history, history[0].Identity, history.Length))
            {
                return false;
            }
        }

        entries = read.Entries;
        return true;
    }

    private static bool PriorOperationMatchesRequest(
        GovernedModelUsageLedgerIdentity identity,
        GovernedModelAttemptAdmissionRequest request,
        GovernedModelProfilePin primary,
        GovernedModelBudgetPolicy effectiveBudget)
        => string.Equals(identity.WorkspaceId, request.RoutingAdmission.WorkspaceId, StringComparison.Ordinal)
            && string.Equals(identity.RunId, request.RunId, StringComparison.Ordinal)
            && string.Equals(identity.GraphId, request.RoutingAdmission.GraphId, StringComparison.Ordinal)
            && string.Equals(identity.GraphRevisionId, request.RoutingAdmission.GraphRevisionId, StringComparison.Ordinal)
            && string.Equals(identity.GraphExecutableHash, request.RoutingAdmission.GraphExecutableHash, StringComparison.Ordinal)
            && identity.ExecutionGeneration == request.ExecutionGeneration
            && string.Equals(identity.AdmissionReceiptHash, request.AdmissionReceipt.ContentHash, StringComparison.Ordinal)
            && string.Equals(identity.RoutingAdmissionHash, request.RoutingAdmission.ContentHash, StringComparison.Ordinal)
            && string.Equals(identity.NodeId, request.NodeId, StringComparison.Ordinal)
            && identity.PlanOrdinal == request.PlanOrdinal
            && identity.ActivationOrdinal == request.ActivationOrdinal
            && identity.VisitOrdinal == request.VisitOrdinal
            && string.Equals(identity.AttemptOperationId, request.AttemptOperationId, StringComparison.Ordinal)
            && identity.AttemptNumber == request.AttemptNumber
            && string.Equals(identity.ProfilePinHash, primary.ContentHash, StringComparison.Ordinal)
            && string.Equals(identity.BudgetPolicyHash, effectiveBudget.ContentHash, StringComparison.Ordinal);

    private static GovernedModelAttemptAdmissionResult PriorOperationResult(
        GovernedModelAttemptAdmissionStatus status,
        GovernedModelProfilePin primary,
        IReadOnlyList<GovernedModelUsageLedgerEntry> entries)
        => new(
            status,
            primary,
            entries[0],
            entries[^1],
            entries.Any(entry => entry.Phase == GovernedModelUsageLedgerPhase.DispatchBoundaryReached));

    private static GovernedModelAttemptAdmissionResult ConflictingPriorOperationResult(
        GovernedModelProfilePin primary,
        IReadOnlyList<GovernedModelUsageLedgerEntry> entries)
        => entries.Any(entry => entry.Phase == GovernedModelUsageLedgerPhase.DispatchBoundaryReached)
            ? PriorOperationResult(GovernedModelAttemptAdmissionStatus.Conflict, primary, entries)
            : Result(GovernedModelAttemptAdmissionStatus.Conflict);

    private static PriorOperationReadResult PriorOperationNotFound()
        => new(PriorOperationReadStatus.NotFound, null);

    private static PriorOperationReadResult PriorOperationUnavailable()
        => new(PriorOperationReadStatus.Unavailable, null);

    private static bool TryValidateRequest(GovernedModelAttemptAdmissionRequest? request, out GovernedModelRoutingAdmissionEntry? node)
    {
        node = null;
        try
        {
            if (request is null || !GovernedModelContractValidator.IsValid(request.RoutingAdmission)
                || !GovernedLoopAdmissionValidator.Validate(request.AdmissionReceipt).IsValid
                || !GovernedLoopAdmissionContractHash.Matches(request.AdmissionReceipt)
                || request.AttemptNumber is < 1 or > GovernedLoopExecutionLimits.MaxNodeAttempt
                || request.PlanOrdinal is < 0 or >= GovernedLoopExecutionLimits.MaxFrontierNodes
                || request.ActivationOrdinal is < 0 or >= GovernedLoopExecutionLimits.MaxFrontierNodes
                || request.VisitOrdinal is < 1 or > GovernedLoopExecutionLimits.MaxNodeVisits
                || !ModelProfileCatalogService.IsHash(request.RequestedPrimaryPinHash)
                || request.RetryUsageCeiling is not null && !GovernedModelContractValidator.IsValid(request.RetryUsageCeiling)
                || !string.Equals(request.AdmissionReceipt.Evidence.ModelRoutingAdmission.ContentHash, request.RoutingAdmission.ContentHash, StringComparison.Ordinal)
                || !string.Equals(request.AdmissionReceipt.Evidence.Binding.RunId, request.RunId, StringComparison.Ordinal)
                || request.AdmissionReceipt.Evidence.Binding.ExecutionGeneration != request.ExecutionGeneration
                || !string.Equals(request.RunId, request.RoutingAdmission.RunId, StringComparison.Ordinal)
                || request.ExecutionGeneration != request.RoutingAdmission.ExecutionGeneration)
            {
                return false;
            }
            var matches = request.RoutingAdmission.Entries.Where(value => string.Equals(value.NodeId, request.NodeId, StringComparison.Ordinal)).Take(2).ToArray();
            if (matches.Length != 1 || !string.Equals(matches[0].NodeTypeId, request.NodeTypeId, StringComparison.Ordinal))
            {
                return false;
            }
            node = matches[0];
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsValidReservationResult(GovernedModelUsageReservationResult? result, GovernedModelUsageLedgerIdentity identity, GovernedModelProfilePin primary)
        => result is not null && Enum.IsDefined(result.Status) && result.Status != 0 && result.Generation >= 0
            && (result.Status is GovernedModelUsageLedgerAppendStatus.Appended or GovernedModelUsageLedgerAppendStatus.AlreadyPresent
                ? result.Generation > 0
                    && GovernedModelContractValidator.IsValid(result.ReservationEntry)
                    && result.ReservationEntry!.Phase == GovernedModelUsageLedgerPhase.ReservationCommitted
                    && result.ReservationEntry.Generation == 1
                    && string.Equals(result.ReservationEntry.Identity.ContentHash, identity.ContentHash, StringComparison.Ordinal)
                    && string.Equals(result.ReservationEntry.EvidenceHash, primary.ContentHash, StringComparison.Ordinal)
                : result.ReservationEntry is null);

    private static bool IsValidMetadataRead(ModelProfileSourceReadResult? read)
        => read is not null && Enum.IsDefined(read.Status) && read.Status != 0
            && (read.Status == ModelProfileSourceReadStatus.Found && GovernedModelContractValidator.IsValid(read.Metadata) && ModelProfileCatalogService.IsHash(read.SourceRevisionHash)
                || read.Status is ModelProfileSourceReadStatus.NotFound or ModelProfileSourceReadStatus.Unavailable && read.Metadata is null && read.SourceRevisionHash is null);

    private static bool IsValidDataPosture(
        ModelInferenceDataPosture? data,
        GovernedModelAttemptAdmissionRequest request,
        string inputPayloadHash)
    {
        if (data is null || !Enum.IsDefined(data.Status) || data.Status == 0
            || !string.Equals(data.RunId, request.RunId, StringComparison.Ordinal)
            || !string.Equals(data.NodeId, request.NodeId, StringComparison.Ordinal)
            || data.PlanOrdinal != request.PlanOrdinal
            || data.ActivationOrdinal != request.ActivationOrdinal
            || data.VisitOrdinal != request.VisitOrdinal
            || data.AttemptNumber != request.AttemptNumber
            || !string.Equals(data.AttemptOperationId, request.AttemptOperationId, StringComparison.Ordinal)
            || !string.Equals(data.InputPayloadHash, inputPayloadHash, StringComparison.Ordinal))
        {
            return false;
        }
        return data.Status == ModelInferenceDataPostureStatus.Available
            ? ModelProfileCatalogService.IsHash(data.EvidenceHash) && IsCanonicalDataClasses(data.DataClasses)
            : data.EvidenceHash is null && data.DataClasses.Count == 0;
    }

    private static bool IsValidAuthority(ModelAttemptAuthorityEvidence? authority, GovernedModelAttemptAdmissionRequest request, GovernedModelRoutingAdmissionEntry node, GovernedModelProfilePin primary)
        => authority is not null && Enum.IsDefined(authority.Status) && authority.Status != 0
            && string.Equals(authority.RoutingAdmissionHash, request.RoutingAdmission.ContentHash, StringComparison.Ordinal)
            && string.Equals(authority.RunId, request.RunId, StringComparison.Ordinal)
            && authority.ExecutionGeneration == request.ExecutionGeneration
            && string.Equals(authority.OwningRoleId, request.RoutingAdmission.OwningRoleId, StringComparison.Ordinal)
            && string.Equals(authority.NodeId, node.NodeId, StringComparison.Ordinal)
            && string.Equals(authority.PrimaryPinHash, primary.ContentHash, StringComparison.Ordinal)
            && authority.PlanOrdinal == request.PlanOrdinal
            && authority.ActivationOrdinal == request.ActivationOrdinal
            && authority.VisitOrdinal == request.VisitOrdinal
            && authority.AttemptNumber == request.AttemptNumber
            && string.Equals(authority.AttemptOperationId, request.AttemptOperationId, StringComparison.Ordinal)
            && (authority.Status == ModelAttemptAuthorityStatus.Allowed ? ModelProfileCatalogService.IsHash(authority.EvidenceHash) : authority.Status == ModelAttemptAuthorityStatus.Denied ? ModelProfileCatalogService.IsHash(authority.EvidenceHash) : authority.EvidenceHash is null);

    private static bool IsCanonicalDataClasses(IReadOnlyList<CapabilityDataClass> values)
    {
        try
        {
            return values.Count <= CapabilityContractLimits.MaxDataClasses
                && values.All(value => CapabilityDataClass.TryParse(value.Value, out var parsed, out _) && value.Equals(parsed))
                && values.Select(value => value.Value).SequenceEqual(values.Select(value => value.Value).Order(StringComparer.Ordinal), StringComparer.Ordinal)
                && values.Select(value => value.Value).Distinct(StringComparer.Ordinal).Count() == values.Count;
        }
        catch
        {
            return false;
        }
    }

    private static bool PinMatchesCatalog(CapabilityAdmissionPin pin, CapabilityCatalogEntry entry)
        => CapabilityAdmissionPinValidator.IsValid(pin)
            && Equals(pin.DescriptorIdentity, entry.Lifecycle.DescriptorIdentity)
            && pin.Kind == entry.Descriptor.Kind
            && Equals(pin.Implementation, entry.Descriptor.Implementation)
            && Equals(pin.Provenance, entry.Descriptor.Provenance)
            && string.Equals(pin.SafeDescription, entry.Descriptor.Purpose, StringComparison.Ordinal);

    private static bool IsReady(CapabilityLifecycleSnapshot lifecycle)
        => lifecycle.Declaration == CapabilityDeclarationState.Declared && lifecycle.Installation == CapabilityInstallationState.Installed && lifecycle.Enablement == CapabilityEnablementState.Enabled && lifecycle.Trust == CapabilityTrustState.Verified && lifecycle.Health == CapabilityHealthState.Healthy && lifecycle.Retirement == CapabilityRetirementState.Active;

    private static GovernedModelAttemptAdmissionResult Result(GovernedModelAttemptAdmissionStatus status) => new(status, null, null);
    private static CurrentAttemptResult Current(GovernedModelAttemptAdmissionStatus status) => new(status, null);

    private enum ExactCapabilityStatus { Available, Unavailable }
    private sealed record ExactCapabilityResult(ExactCapabilityStatus Status, CapabilityCatalogEntry? Entry);
    private sealed record CurrentAttemptResult(GovernedModelAttemptAdmissionStatus Status, GovernedModelCurrentAttemptEvidence? Evidence);

    private enum PriorOperationReadStatus
    {
        NotFound = 1,
        Found = 2,
        Unavailable = 3,
    }

    private sealed record PriorOperationReadResult(
        PriorOperationReadStatus Status,
        IReadOnlyList<GovernedModelUsageLedgerEntry>? Entries);
}
