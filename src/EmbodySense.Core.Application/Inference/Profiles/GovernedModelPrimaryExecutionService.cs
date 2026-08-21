using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Application.Inference.Profiles.Models;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Common.Inference.Profiles;
using EmbodySense.Core.Common.Inference.Profiles.Models;

namespace EmbodySense.Core.Application.Inference.Profiles;

/// <summary>Executes only the admitted primary through one fresh exact client and durable transport/usage evidence.</summary>
public sealed class GovernedModelPrimaryExecutionService : IGovernedModelPrimaryExecutionService
{
    private readonly GovernedModelAttemptAdmissionService _admission;
    private readonly GovernedModelUsageReconciliationService _usage;
    private readonly IExactModelProfileInferenceClientResolver _resolver;
    private readonly IGovernedModelPrimaryExecutionBoundaryObserver? _boundaryObserver;

    /// <summary>Creates the primary-only execution service.</summary>
    public GovernedModelPrimaryExecutionService(
        GovernedModelAttemptAdmissionService admission,
        GovernedModelUsageReconciliationService usage,
        IExactModelProfileInferenceClientResolver resolver,
        IGovernedModelPrimaryExecutionBoundaryObserver? boundaryObserver = null)
    {
        _admission = admission ?? throw new ArgumentNullException(nameof(admission));
        _usage = usage ?? throw new ArgumentNullException(nameof(usage));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _boundaryObserver = boundaryObserver;
    }

    /// <summary>Reserves, resolves the exact primary, fences transport, and durably retains/reconciles explicit usage.</summary>
    public async Task<GovernedModelPrimaryExecutionResult> ExecuteAsync(
        GovernedModelPrimaryExecutionRequest? request,
        InferenceProviderTransportCommitBoundary providerAuthorityBoundary,
        Func<string, CancellationToken, Task>? responseChunkHandler = null,
        Action? providerRequestStarted = null,
        CancellationToken cancellationToken = default)
    {
        if (request?.Admission is null || request.InferenceRequest is null || providerAuthorityBoundary is null)
        {
            return Result(GovernedModelAttemptAdmissionStatus.Invalid);
        }

        await ObserveAsync(GovernedModelPrimaryExecutionBoundary.BeforeReservation, cancellationToken).ConfigureAwait(false);
        var admitted = await _admission.ReserveAsync(request.Admission, request.InferenceRequest, cancellationToken).ConfigureAwait(false);
        if (admitted.Status == GovernedModelAttemptAdmissionStatus.AlreadyAdvanced
            && admitted.Primary is not null
            && admitted.ReservationEntry is not null
            && admitted.CurrentEntry is not null)
        {
            return EvidenceResult(admitted, admitted.Status, null, null, null, admitted.CurrentEntry);
        }
        if (admitted.Status is not GovernedModelAttemptAdmissionStatus.Reserved and not GovernedModelAttemptAdmissionStatus.Replayed
            && admitted.Primary is not null
            && admitted.ReservationEntry is not null
            && admitted.CurrentEntry is not null)
        {
            if (admitted.ProviderDispatchMayHaveOccurred)
            {
                return EvidenceResult(admitted, admitted.Status, null, null, null, admitted.CurrentEntry);
            }

            return ResultWithNotStartedProof(
                admitted,
                admitted.Status,
                await ProveNotStartedAsync(admitted.ReservationEntry).ConfigureAwait(false));
        }
        if (admitted.Status is not GovernedModelAttemptAdmissionStatus.Reserved and not GovernedModelAttemptAdmissionStatus.Replayed || admitted.Primary is null || admitted.ReservationEntry is null)
        {
            return Result(admitted.Status);
        }
        await ObserveAsync(GovernedModelPrimaryExecutionBoundary.ReservationRetained, cancellationToken).ConfigureAwait(false);

        var identity = admitted.ReservationEntry.Identity;
        var providerAttemptId = identity.AttemptOperationId;
        var providerCorrelationId = identity.ContentHash;
        if (!IsCallerCorrelationCompatible(request.InferenceRequest.Correlation, providerAttemptId, providerCorrelationId))
        {
            return ResultWithNotStartedProof(
                admitted,
                GovernedModelAttemptAdmissionStatus.Invalid,
                await ProveNotStartedAsync(admitted.ReservationEntry).ConfigureAwait(false));
        }

        var matchingNodes = request.Admission.RoutingAdmission.Entries
            .Where(entry => string.Equals(entry.NodeId, request.Admission.NodeId, StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        if (matchingNodes.Length != 1)
        {
            return ResultWithNotStartedProof(
                admitted,
                GovernedModelAttemptAdmissionStatus.Invalid,
                await ProveNotStartedAsync(admitted.ReservationEntry).ConfigureAwait(false));
        }

        var node = matchingNodes[0];
        if (!GovernedModelBudgetPolicy.TryRestrictPerAttempt(node.Requirements.Budget, request.Admission.RetryUsageCeiling, out var effectiveBudget))
        {
            return ResultWithNotStartedProof(
                admitted,
                GovernedModelAttemptAdmissionStatus.Invalid,
                await ProveNotStartedAsync(admitted.ReservationEntry).ConfigureAwait(false));
        }
        var resolverRequest = new ExactModelProfileInferenceClientRequest(
            admitted.Primary,
            identity,
            admitted.ReservationEntry.Reservation!,
            effectiveBudget!,
            request.Admission.RoutingAdmission.ContentHash,
            request.Admission.AdmissionReceipt.ContentHash,
            identity.AuthorityEvidenceHash,
            identity.DataPostureEvidenceHash,
            providerAttemptId,
            providerCorrelationId,
            request.ToolBroker);
        EmbodySense.Core.Common.Inference.LlmInferenceRequest exactInferenceRequest;
        try
        {
            exactInferenceRequest = BuildExactInferenceRequest(request.InferenceRequest, admitted.ReservationEntry.Reservation!, admitted.Primary.Metadata.MaximumOutputTokens, providerAttemptId, providerCorrelationId);
        }
        catch
        {
            return ResultWithNotStartedProof(
                admitted,
                GovernedModelAttemptAdmissionStatus.Invalid,
                await ProveNotStartedAsync(admitted.ReservationEntry).ConfigureAwait(false));
        }

        ExactModelProfileInferenceClientResolution resolution;
        try
        {
            resolution = await _resolver.ResolveAsync(resolverRequest, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await ProveNotStartedAsync(admitted.ReservationEntry).ConfigureAwait(false);
            throw;
        }
        catch
        {
            return ResultWithNotStartedProof(
                admitted,
                GovernedModelAttemptAdmissionStatus.Unavailable,
                await ProveNotStartedAsync(admitted.ReservationEntry).ConfigureAwait(false));
        }

        if (!IsValidResolution(resolution, resolverRequest))
        {
            var rejected = await RejectResolutionAsync(admitted.ReservationEntry, resolution?.Lease).ConfigureAwait(false);
            var result = ResultWithNotStartedProof(admitted, GovernedModelAttemptAdmissionStatus.Unavailable, rejected.Proof);
            return rejected.Attention is null
                ? result
                : result with { ReconciliationStatus = rejected.Attention.Status, TerminalUsageEntry = rejected.Attention.Entry };
        }
        if (resolution.Status != ExactModelProfileInferenceClientResolutionStatus.Resolved)
        {
            var intended = resolution.Status == ExactModelProfileInferenceClientResolutionStatus.Ineligible
                ? GovernedModelAttemptAdmissionStatus.Ineligible
                : GovernedModelAttemptAdmissionStatus.Unavailable;
            return ResultWithNotStartedProof(
                admitted,
                intended,
                await ProveNotStartedAsync(admitted.ReservationEntry).ConfigureAwait(false));
        }

        var lease = resolution.Lease!;
        var boundaryClaim = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var dispatchEvidenceHash = AttemptEvidenceHash("embodysense.model-provider-dispatch-boundary.v1", admitted.ReservationEntry.Identity, admitted.ReservationEntry.ContentHash, boundaryClaim);
        var boundaryReached = false;
        var priorBoundaryAdvanced = false;
        var duplicateBoundaryInvocation = false;
        var boundaryInvocation = 0;
        GovernedModelUsageTransitionResult? dispatch = null;
        var usageConclusive = false;
        var responseBuffer = new BoundedInferenceResponseBuffer();
        try
        {
            var response = await lease.Client.GenerateAsync(
                exactInferenceRequest,
                responseBuffer.AppendAsync,
                cancellationToken,
                async (commitTransport, token) =>
                {
                    if (Interlocked.CompareExchange(ref boundaryInvocation, 1, 0) != 0)
                    {
                        duplicateBoundaryInvocation = true;
                        throw new ProviderBoundaryAlreadyAdvancedException();
                    }
                    var current = await _admission.ReserveAsync(request.Admission, request.InferenceRequest, token).ConfigureAwait(false);
                    if (!IsExactPreTransportReplay(current, admitted))
                    {
                        throw new InvalidOperationException("Current model-attempt authority changed before provider transport.");
                    }
                    await providerAuthorityBoundary(
                        async authorityToken =>
                        {
                            dispatch = await _usage.RecordDispatchAsync(new GovernedModelDispatchEvidenceRequest(admitted.ReservationEntry.Identity, admitted.ReservationEntry.ContentHash, true, dispatchEvidenceHash), authorityToken).ConfigureAwait(false);
                            if (dispatch.Status != GovernedModelUsageTransitionStatus.Applied)
                            {
                                priorBoundaryAdvanced = dispatch.Status is GovernedModelUsageTransitionStatus.Replayed or GovernedModelUsageTransitionStatus.Conflict;
                                throw priorBoundaryAdvanced ? new ProviderBoundaryAlreadyAdvancedException() : new InvalidOperationException("The provider transport boundary could not be durably retained.");
                            }
                            boundaryReached = true;
                            providerRequestStarted?.Invoke();
                            await commitTransport(authorityToken).ConfigureAwait(false);
                            await ObserveAsync(GovernedModelPrimaryExecutionBoundary.ProviderTransportCommitted, authorityToken).ConfigureAwait(false);
                        },
                        token).ConfigureAwait(false);
                }).ConfigureAwait(false);
            await ObserveAsync(GovernedModelPrimaryExecutionBoundary.ProviderResponseReceived, cancellationToken).ConfigureAwait(false);
            if (!boundaryReached || dispatch is null
                || !GovernedModelContractValidator.IsValidProviderResponse(response, lease.Enforcement.ExpectedProviderId, admitted.Primary.Metadata.ModelId, lease.Enforcement.ExpectedResponseSurface)
                || !responseBuffer.TrySeal(response.OutputText, out var exactChunks))
            {
                throw new InvalidOperationException("Provider transport or explicit usage evidence was incomplete.");
            }
            if (!UsageConformsToProfile(response.Usage, admitted.Primary.Metadata.UsageSupport))
            {
                throw new InvalidOperationException("Provider usage evidence exceeded the exact profile's declared evidence support.");
            }

            var providerEvidenceHash = ResponseEvidenceHash(admitted.ReservationEntry.Identity, response);
            var observed = await _usage.ObserveUsageAsync(new GovernedModelUsageObservationRequest(admitted.ReservationEntry.Identity, admitted.ReservationEntry.ContentHash, response.Usage, providerEvidenceHash), cancellationToken).ConfigureAwait(false);
            if (observed.Status is not GovernedModelUsageTransitionStatus.Applied and not GovernedModelUsageTransitionStatus.Replayed)
            {
                if (observed.Status == GovernedModelUsageTransitionStatus.AttentionRequired)
                {
                    return EvidenceResult(admitted, GovernedModelAttemptAdmissionStatus.AlreadyAdvanced, dispatch.Status, observed.Status, observed.Status, observed.Entry);
                }
                var attention = await RequirePostBoundaryAttentionAsync(admitted.ReservationEntry, "embodysense.model-provider-usage-retention-failed.v1", providerEvidenceHash).ConfigureAwait(false);
                return EvidenceResult(admitted, GovernedModelAttemptAdmissionStatus.AlreadyAdvanced, dispatch.Status, observed.Status, attention.Status, attention.Entry);
            }
            await ObserveAsync(GovernedModelPrimaryExecutionBoundary.UsageRetained, cancellationToken).ConfigureAwait(false);
            var reconciled = await _usage.ReconcileAsync(admitted.ReservationEntry.Identity, cancellationToken).ConfigureAwait(false);
            if (reconciled.Status == GovernedModelUsageTransitionStatus.AttentionRequired)
            {
                return EvidenceResult(admitted, GovernedModelAttemptAdmissionStatus.AlreadyAdvanced, dispatch.Status, observed.Status, reconciled.Status, reconciled.Entry);
            }
            if (reconciled.Status is not GovernedModelUsageTransitionStatus.Applied
                and not GovernedModelUsageTransitionStatus.Replayed)
            {
                var attention = await RequirePostBoundaryAttentionAsync(admitted.ReservationEntry, "embodysense.model-provider-reconciliation-failed.v1", providerEvidenceHash).ConfigureAwait(false);
                return EvidenceResult(admitted, GovernedModelAttemptAdmissionStatus.AlreadyAdvanced, dispatch.Status, observed.Status, attention.Status, attention.Entry);
            }
            usageConclusive = true;
            if (responseChunkHandler is not null)
            {
                if (!responseBuffer.IsExactSealedChunks(exactChunks))
                {
                    throw new GovernedModelResponsePublicationException();
                }
                try
                {
                    foreach (var chunk in exactChunks)
                    {
                        await responseChunkHandler(chunk, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (GovernedModelResponsePublicationException)
                {
                    throw;
                }
                catch
                {
                    throw new GovernedModelResponsePublicationException();
                }
                if (!responseBuffer.IsExactSealedChunks(exactChunks))
                {
                    throw new GovernedModelResponsePublicationException();
                }
            }
            return EvidenceResult(admitted, admitted.Status, dispatch.Status, observed.Status, reconciled.Status, reconciled.Entry, response);
        }
        catch
        {
            if (priorBoundaryAdvanced)
            {
                return EvidenceResult(admitted, GovernedModelAttemptAdmissionStatus.AlreadyAdvanced, dispatch?.Status, null, null, dispatch?.Entry);
            }
            if (duplicateBoundaryInvocation)
            {
                var duplicate = AttemptEvidenceHash("embodysense.model-provider-duplicate-transport-boundary.v1", admitted.ReservationEntry.Identity, admitted.ReservationEntry.ContentHash, boundaryClaim);
                var attention = await _usage.RequireAttentionAsync(
                    new GovernedModelAmbiguousUsageRequest(admitted.ReservationEntry.Identity, admitted.ReservationEntry.ContentHash, duplicate),
                    CancellationToken.None).ConfigureAwait(false);
                return EvidenceResult(admitted, GovernedModelAttemptAdmissionStatus.AlreadyAdvanced, dispatch?.Status, null, attention.Status, attention.Entry);
            }
            if (usageConclusive)
            {
                throw new GovernedModelResponsePublicationException();
            }
            if (!boundaryReached)
            {
                await ProveNotStartedAsync(admitted.ReservationEntry).ConfigureAwait(false);
            }
            else
            {
                var ambiguity = AttemptEvidenceHash("embodysense.model-provider-usage-ambiguous.v1", admitted.ReservationEntry.Identity, admitted.ReservationEntry.ContentHash, boundaryClaim);
                _ = await _usage.RequireAttentionAsync(new GovernedModelAmbiguousUsageRequest(admitted.ReservationEntry.Identity, admitted.ReservationEntry.ContentHash, ambiguity), CancellationToken.None).ConfigureAwait(false);
            }
            throw;
        }
        finally
        {
            try
            {
                await lease.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                if (boundaryReached && !usageConclusive)
                {
                    var ambiguity = AttemptEvidenceHash("embodysense.model-provider-lease-disposal-failed.v1", admitted.ReservationEntry.Identity, admitted.ReservationEntry.ContentHash, boundaryClaim);
                    _ = await _usage.RequireAttentionAsync(new GovernedModelAmbiguousUsageRequest(admitted.ReservationEntry.Identity, admitted.ReservationEntry.ContentHash, ambiguity), CancellationToken.None).ConfigureAwait(false);
                }
                // Cleanup cannot weaken already-conclusive provider usage or replace the retained response.
            }
        }
    }

    private ValueTask ObserveAsync(
        GovernedModelPrimaryExecutionBoundary boundary,
        CancellationToken cancellationToken)
        => _boundaryObserver is null
            ? ValueTask.CompletedTask
            : _boundaryObserver.ObserveAsync(boundary, cancellationToken);

    private Task<GovernedModelUsageTransitionResult> ProveNotStartedAsync(GovernedModelUsageLedgerEntry reservation)
    {
        var evidence = AttemptEvidenceHash("embodysense.model-provider-dispatch-not-started.v1", reservation.Identity, reservation.ContentHash, reservation.Identity.ContentHash);
        return _usage.RecordDispatchAsync(new GovernedModelDispatchEvidenceRequest(reservation.Identity, reservation.ContentHash, false, evidence), CancellationToken.None);
    }

    private async Task<(GovernedModelUsageTransitionResult Proof, GovernedModelUsageTransitionResult? Attention)> RejectResolutionAsync(GovernedModelUsageLedgerEntry reservation, IExactModelProfileInferenceClientLease? suspiciousLease)
    {
        var cleanupFailed = false;
        if (suspiciousLease is not null)
        {
            try
            {
                await suspiciousLease.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                cleanupFailed = true;
            }
        }

        var proof = await ProveNotStartedAsync(reservation).ConfigureAwait(false);
        if (cleanupFailed)
        {
            var attention = await RequirePostBoundaryAttentionAsync(
                reservation,
                "embodysense.model-provider-invalid-lease-disposal-failed.v1",
                reservation.Identity.ContentHash).ConfigureAwait(false);
            return (proof, attention);
        }
        return (proof, null);
    }

    private Task<GovernedModelUsageTransitionResult> RequirePostBoundaryAttentionAsync(GovernedModelUsageLedgerEntry reservation, string domain, string exactEvidenceSeed)
    {
        var evidence = AttemptEvidenceHash(domain, reservation.Identity, reservation.ContentHash, exactEvidenceSeed);
        return _usage.RequireAttentionAsync(new GovernedModelAmbiguousUsageRequest(reservation.Identity, reservation.ContentHash, evidence), CancellationToken.None);
    }

    private static bool IsValidResolution(ExactModelProfileInferenceClientResolution? resolution, ExactModelProfileInferenceClientRequest request)
    {
        if (resolution is null || !Enum.IsDefined(resolution.Status) || resolution.Status == 0)
        {
            return false;
        }
        if (resolution.Status != ExactModelProfileInferenceClientResolutionStatus.Resolved)
        {
            return resolution.Lease is null;
        }
        try
        {
            return resolution.Lease?.Client is not null
                && string.Equals(resolution.Lease.ProfilePinHash, request.Primary.ContentHash, StringComparison.Ordinal)
                && string.Equals(resolution.Lease.ConfigurationHash, request.Primary.Metadata.ConfigurationHash, StringComparison.Ordinal)
                && IsExactEnforcement(resolution.Lease.Enforcement, request);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsExactPreTransportReplay(
        GovernedModelAttemptAdmissionResult current,
        GovernedModelAttemptAdmissionResult admitted)
        => current.Status == GovernedModelAttemptAdmissionStatus.Replayed
            && current.Primary is not null
            && current.ReservationEntry is not null
            && admitted.Primary is not null
            && admitted.ReservationEntry is not null
            && string.Equals(current.Primary.ContentHash, admitted.Primary.ContentHash, StringComparison.Ordinal)
            && string.Equals(current.ReservationEntry.Identity.ContentHash, admitted.ReservationEntry.Identity.ContentHash, StringComparison.Ordinal)
            && string.Equals(current.ReservationEntry.ContentHash, admitted.ReservationEntry.ContentHash, StringComparison.Ordinal);

    private static bool IsExactEnforcement(ExactModelProfileEnforcementAcknowledgement? evidence, ExactModelProfileInferenceClientRequest request)
        => evidence is not null
            && string.Equals(evidence.ProfilePinHash, request.Primary.ContentHash, StringComparison.Ordinal)
            && string.Equals(evidence.AttemptIdentityHash, request.AttemptIdentity.ContentHash, StringComparison.Ordinal)
            && string.Equals(evidence.ReservationHash, request.Reservation.ContentHash, StringComparison.Ordinal)
            && string.Equals(evidence.BudgetPolicyHash, request.BudgetPolicy.ContentHash, StringComparison.Ordinal)
            && string.Equals(evidence.RoutingAdmissionHash, request.RoutingAdmissionHash, StringComparison.Ordinal)
            && string.Equals(evidence.AdmissionReceiptHash, request.AdmissionReceiptHash, StringComparison.Ordinal)
            && string.Equals(evidence.AuthorityEvidenceHash, request.AuthorityEvidenceHash, StringComparison.Ordinal)
            && string.Equals(evidence.DataPostureEvidenceHash, request.DataPostureEvidenceHash, StringComparison.Ordinal)
            && string.Equals(evidence.ExpectedProviderId, request.Primary.Metadata.ProviderId, StringComparison.Ordinal)
            && evidence.ExpectedResponseSurface is LlmInferenceSurface.AzureAiFoundry or LlmInferenceSurface.OpenAiCodex
            && string.Equals(evidence.ProviderAttemptId, request.ProviderAttemptId, StringComparison.Ordinal)
            && string.Equals(evidence.ProviderCorrelationId, request.ProviderCorrelationId, StringComparison.Ordinal)
            && ModelProfileCatalogService.IsHash(evidence.EnforcementEvidenceHash);

    private static bool IsCallerCorrelationCompatible(LlmInferenceCorrelation? correlation, string providerAttemptId, string providerCorrelationId)
        => correlation is null
            || string.Equals(correlation.ProviderAttemptId, providerAttemptId, StringComparison.Ordinal)
                && string.Equals(correlation.ProviderCorrelationId, providerCorrelationId, StringComparison.Ordinal)
                && correlation.ToolAuditCorrelation is null;

    private static EmbodySense.Core.Common.Inference.LlmInferenceRequest BuildExactInferenceRequest(
        EmbodySense.Core.Common.Inference.LlmInferenceRequest caller,
        GovernedModelUsageCeiling reservation,
        int profileMaximumOutputTokens,
        string providerAttemptId,
        string providerCorrelationId)
    {
        if (profileMaximumOutputTokens <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(profileMaximumOutputTokens));
        }
        var hardOutput = reservation.OutputTokens.IsBounded
            ? checked((int)Math.Min(reservation.OutputTokens.Maximum, profileMaximumOutputTokens))
            : profileMaximumOutputTokens;
        var callerOutput = caller.Options.MaxOutputTokenCount;
        if (callerOutput is <= 0)
        {
            throw new ArgumentException("Caller maximum output tokens must be positive when supplied.", nameof(caller));
        }
        var effectiveOutput = callerOutput is null ? hardOutput : Math.Min(callerOutput.Value, hardOutput);
        var options = caller.Options with { MaxOutputTokenCount = effectiveOutput };
        return new EmbodySense.Core.Common.Inference.LlmInferenceRequest(
            caller.Messages,
            options,
            caller.InstructionContext,
            new LlmInferenceCorrelation(providerAttemptId, providerCorrelationId));
    }

    private static bool UsageConformsToProfile(LlmInferenceUsageEvidence usage, GovernedModelUsageSupportPolicy support)
        => Supported(usage.InputTokens.Status, support.InputTokens)
            && Supported(usage.OutputTokens.Status, support.OutputTokens)
            && Supported(usage.CachedTokens.Status, support.CachedTokens)
            && Supported(usage.TotalTokens.Status, support.TotalTokens)
            && Supported(usage.MonetaryCost.Status, support.MonetaryCost);

    private static bool Supported(GovernedModelUsageEvidenceStatus evidence, GovernedModelUsageSupport support)
        => evidence == GovernedModelUsageEvidenceStatus.Unavailable || support is GovernedModelUsageSupport.AuthoritativeAfterDispatch or GovernedModelUsageSupport.AuthoritativeAndHardBoundedAtDispatch;

    private static string ResponseEvidenceHash(GovernedModelUsageLedgerIdentity identity, LlmInferenceResponse response)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "embodysense.model-provider-response-evidence.v1");
        Append(hash, identity.ContentHash);
        Append(hash, ((int)response.Surface).ToString(System.Globalization.CultureInfo.InvariantCulture));
        Append(hash, response.Model ?? string.Empty);
        Append(hash, response.ProviderResponseId ?? string.Empty);
        Append(hash, response.ProviderId ?? string.Empty);
        Append(hash, response.OutputText);
        Append(hash, response.Usage.ContentHash);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string AttemptEvidenceHash(string domain, GovernedModelUsageLedgerIdentity identity, string reservationHash, string boundaryClaim)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, domain);
        Append(hash, identity.ContentHash);
        Append(hash, reservationHash);
        Append(hash, boundaryClaim);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private static GovernedModelPrimaryExecutionResult Result(GovernedModelAttemptAdmissionStatus status) => new(status, null, null, null, null);

    private static GovernedModelPrimaryExecutionResult ResultWithNotStartedProof(
        GovernedModelAttemptAdmissionResult admitted,
        GovernedModelAttemptAdmissionStatus intendedStatus,
        GovernedModelUsageTransitionResult proof)
        => new(
            proof.Status is GovernedModelUsageTransitionStatus.Applied or GovernedModelUsageTransitionStatus.Replayed
                ? intendedStatus
                : GovernedModelAttemptAdmissionStatus.Unavailable,
            null,
            proof.Status,
            null,
            null,
            admitted.Primary,
            admitted.ReservationEntry,
            proof.Entry,
            false);

    private static GovernedModelPrimaryExecutionResult EvidenceResult(
        GovernedModelAttemptAdmissionResult admitted,
        GovernedModelAttemptAdmissionStatus status,
        GovernedModelUsageTransitionStatus? dispatch,
        GovernedModelUsageTransitionStatus? usage,
        GovernedModelUsageTransitionStatus? reconciliation,
        GovernedModelUsageLedgerEntry? terminal,
        LlmInferenceResponse? response = null)
        => new(
            status,
            response,
            dispatch,
            usage,
            reconciliation,
            admitted.Primary,
            admitted.ReservationEntry,
            terminal,
            admitted.ProviderDispatchMayHaveOccurred || TerminalProvesDispatchMayHaveOccurred(terminal));

    private static bool TerminalProvesDispatchMayHaveOccurred(GovernedModelUsageLedgerEntry? terminal)
        => terminal?.Phase is
            GovernedModelUsageLedgerPhase.DispatchBoundaryReached
            or GovernedModelUsageLedgerPhase.UsageObserved
            or GovernedModelUsageLedgerPhase.Reconciled
            || terminal?.Phase == GovernedModelUsageLedgerPhase.AttentionRequired
                && (terminal.UsageUnknown || terminal.Usage is not null);

}
