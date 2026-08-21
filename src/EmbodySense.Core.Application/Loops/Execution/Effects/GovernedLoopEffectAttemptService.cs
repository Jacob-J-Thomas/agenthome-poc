using EmbodySense.Core.Application.Loops.EffectAttempts;
using EmbodySense.Core.Application.Loops.EffectAttempts.Models;
using EmbodySense.Core.Application.Loops.EffectAuthorityEvidence.Models;
using EmbodySense.Core.Application.Loops.Execution.Authority;
using EmbodySense.Core.Application.Loops.Execution.Authority.Models;
using EmbodySense.Core.Application.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Loops.Admission;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Execution.Authority;
using EmbodySense.Core.Common.Loops.Execution.Authority.Models;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Effects;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Capabilities;

namespace EmbodySense.Core.Application.Loops.Execution.Effects;

/// <summary>Implements one canonical durable effect-attempt protocol without selecting retry or reconciliation policy.</summary>
public sealed class GovernedLoopEffectAttemptService : IGovernedLoopEffectAttemptService
{
    private readonly IGovernedActuatorCatalogResolver _catalog;
    private readonly IGovernedLoopEffectAttemptStore _store;
    private readonly IGovernedLoopEffectAuthorityDecisionBoundary _authority;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates one orchestration service over catalog, persistence, and fresh authority ports.</summary>
    public GovernedLoopEffectAttemptService(
        IGovernedActuatorCatalogResolver catalog,
        IGovernedLoopEffectAttemptStore store,
        IGovernedLoopEffectAuthorityDecisionBoundary authority,
        TimeProvider? timeProvider = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<GovernedLoopEffectAttemptExecutionResult> ExecuteAsync(
        GovernedLoopEffectAttemptRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await ExecuteCoreAsync(request, allowPreparationRefresh: true, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (GovernedLoopEffectAttemptEvidenceException)
        {
            return Result(
                GovernedLoopEffectAttemptExecutionStatus.EvidenceUnavailable,
                null,
                "Trusted effect-attempt evidence became unavailable; no safe dispatch posture can be asserted.");
        }
    }

    private async Task<GovernedLoopEffectAttemptExecutionResult> ExecuteCoreAsync(
        GovernedLoopEffectAttemptRequest request,
        bool allowPreparationRefresh,
        CancellationToken cancellationToken)
    {
        if (!IsStructurallyValid(request))
        {
            return Result(GovernedLoopEffectAttemptExecutionStatus.InvalidRequest, null, "The effect attempt request is invalid.");
        }
        if (!GovernedActuatorInputContract.TryCanonicalize(request.InputJson, out var canonicalInput, out var inputFailure))
        {
            return Result(GovernedLoopEffectAttemptExecutionStatus.InvalidRequest, null, inputFailure ?? "The structured actuator input is invalid.");
        }

        var resumed = await ResumeAsync(request.IdempotencyOperationId, request.EffectGeneration, cancellationToken).ConfigureAwait(false);
        if (resumed.Status != GovernedLoopEffectAttemptStoreStatus.NotFound)
        {
            return await ResumeExistingAsync(request, canonicalInput!, resumed, cancellationToken).ConfigureAwait(false);
        }
        if (resumed.Attempt is not null || resumed.Lease is not null)
        {
            resumed.Lease?.Dispose();
            return Result(GovernedLoopEffectAttemptExecutionStatus.EvidenceUnavailable, null, "The durable attempt source returned an incoherent absent result.");
        }
        if (!TryGetUtcNow(out var now))
        {
            return Result(GovernedLoopEffectAttemptExecutionStatus.EvidenceUnavailable, null, "Trusted UTC time is unavailable before intent publication.");
        }

        var (preparation, preparationFailure) = await PrepareDispatchAsync(request, canonicalInput!, cancellationToken).ConfigureAwait(false);
        if (preparation is null)
        {
            return preparationFailure!;
        }

        GovernedLoopEffectAttempt prepared;
        try
        {
            prepared = GovernedLoopEffectAttemptContract.Prepare(
                request.ExecutionBinding,
                request.NodeId,
                request.NodeAttempt,
                request.CapabilityPin.DescriptorIdentity,
                request.CapabilityPin.Implementation,
                request.ActuatorOperationId,
                preparation.Descriptor.ContentHash,
                request.EffectId,
                request.IdempotencyOperationId,
                request.EffectGeneration,
                preparation.Input.Fingerprint,
                preparation.Evidence.TargetFingerprint,
                preparation.Evidence.PreconditionEvidenceHash,
                request.AdmissionReceipt.ContentHash,
                preparation.Evidence.BeforeEvidenceId,
                now);
        }
        catch (ArgumentException)
        {
            return Result(GovernedLoopEffectAttemptExecutionStatus.InvalidRequest, null, "The exact effect intent could not be constructed canonically.");
        }

        var begun = await BeginAsync(preparation, prepared, cancellationToken).ConfigureAwait(false);
        if (begun.Status == GovernedLoopEffectAttemptStoreStatus.PreparationExpired)
        {
            return allowPreparationRefresh
                ? await ExecuteCoreAsync(request, allowPreparationRefresh: false, cancellationToken).ConfigureAwait(false)
                : Result(GovernedLoopEffectAttemptExecutionStatus.EvidenceUnavailable, null, "The server-derived preparation expired before intent publication.");
        }
        return await ContinueBegunAsync(request, preparation, prepared, begun, cancellationToken).ConfigureAwait(false);
    }

    private async Task<GovernedLoopEffectAttemptExecutionResult> ResumeExistingAsync(
        GovernedLoopEffectAttemptRequest request,
        GovernedActuatorInputEvidence input,
        GovernedLoopEffectAttemptStoreResult resumed,
        CancellationToken cancellationToken)
    {
        if (resumed.Status == GovernedLoopEffectAttemptStoreStatus.OperationInProgress)
        {
            if (resumed.Lease is not null)
            {
                resumed.Lease.Dispose();
                return Result(GovernedLoopEffectAttemptExecutionStatus.EvidenceUnavailable, resumed.Attempt, "The durable attempt source returned impossible concurrent ownership.");
            }
            if (!IsCoherentRetainedIdentity(resumed.Attempt, request, input, out var storeEvidenceInvalid))
            {
                return Result(storeEvidenceInvalid ? GovernedLoopEffectAttemptExecutionStatus.EvidenceUnavailable : GovernedLoopEffectAttemptExecutionStatus.Conflict, resumed.Attempt, "The retained operation identity or authorized content is not exact.");
            }
            return Result(GovernedLoopEffectAttemptExecutionStatus.OperationInProgress, resumed.Attempt, "Another executor owns the exact effect generation.");
        }
        if (resumed.Status == GovernedLoopEffectAttemptStoreStatus.Backpressured
            && resumed.Attempt is null
            && resumed.Lease is null)
        {
            return Result(GovernedLoopEffectAttemptExecutionStatus.Backpressured, null, "Durable effect-attempt capacity is exhausted.");
        }
        var retainedIsCoherent = IsCoherentRetainedIdentity(resumed.Attempt, request, input, out var retainedStoreEvidenceInvalid);
        if (resumed.Status is GovernedLoopEffectAttemptStoreStatus.Corrupt or GovernedLoopEffectAttemptStoreStatus.Unavailable
            || resumed.Status != GovernedLoopEffectAttemptStoreStatus.Replayed
            || !retainedIsCoherent)
        {
            resumed.Lease?.Dispose();
            return Result(
                retainedStoreEvidenceInvalid ? GovernedLoopEffectAttemptExecutionStatus.EvidenceUnavailable : GovernedLoopEffectAttemptExecutionStatus.Conflict,
                resumed.Attempt,
                retainedStoreEvidenceInvalid ? "Durable attempt evidence is unavailable or incoherent." : "The stable effect generation was reused with conflicting authorized content.");
        }

        var current = resumed.Attempt!;
        if (DoesNotRequireOwner(current.Payload.Phase))
        {
            if (resumed.Lease is not null)
            {
                resumed.Lease.Dispose();
                return Result(GovernedLoopEffectAttemptExecutionStatus.EvidenceUnavailable, current, "A terminal attempt returned impossible execution ownership.");
            }
            return ReplayResult(current);
        }
        if (resumed.Lease is null)
        {
            return Result(GovernedLoopEffectAttemptExecutionStatus.EvidenceUnavailable, current, "The unfinished effect attempt did not return execution ownership.");
        }

        using var lease = resumed.Lease;
        if (current.Payload.Phase == GovernedLoopEffectPhase.OutcomeObserved)
        {
            return await CommitObservedAsync(current, lease, cancellationToken).ConfigureAwait(false);
        }
        if (current.Payload.Phase == GovernedLoopEffectPhase.DispatchBoundaryReached)
        {
            return await ProbeCrossedAsync(request, input, current, lease, cancellationToken).ConfigureAwait(false);
        }
        if (current.Payload.Phase != GovernedLoopEffectPhase.IntentPrepared)
        {
            return Result(GovernedLoopEffectAttemptExecutionStatus.EvidenceUnavailable, current, "The retained unfinished phase is unsupported.");
        }
        if (current.DispatchAuthorityEvidenceHash is not null)
        {
            return await MarkPriorAuthorityAmbiguousAsync(current, current.DispatchAuthorityEvidenceHash, lease, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var (preparation, _) = await PrepareDispatchAsync(request, input, cancellationToken).ConfigureAwait(false);
            if (preparation is null)
            {
                return await StopBeforeDispatchAsync(
                    current,
                    lease,
                    "The retained intent could not be safely re-prepared before dispatch.",
                    cancellationToken).ConfigureAwait(false);
            }
            if (!string.Equals(current.OperationDescriptorHash, preparation.Descriptor.ContentHash, StringComparison.Ordinal)
                || !string.Equals(current.TargetFingerprint, preparation.Evidence.TargetFingerprint, StringComparison.Ordinal)
                || !string.Equals(current.PreconditionEvidenceHash, preparation.Evidence.PreconditionEvidenceHash, StringComparison.Ordinal)
                || !string.Equals(current.BeforeEvidenceId, preparation.Evidence.BeforeEvidenceId, StringComparison.Ordinal))
            {
                return await StopBeforeDispatchAsync(current, lease, "The exact server preparation changed before dispatch.", cancellationToken).ConfigureAwait(false);
            }
            return await DispatchOwnedAsync(request, preparation, current, lease, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return await StopBeforeDispatchAsync(current, lease, "Cancellation was proved before the actuator boundary.", CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task<GovernedLoopEffectAttemptExecutionResult> ContinueBegunAsync(
        GovernedLoopEffectAttemptRequest request,
        GovernedActuatorDispatchPreparation preparation,
        GovernedLoopEffectAttempt prepared,
        GovernedLoopEffectAttemptStoreResult begun,
        CancellationToken cancellationToken)
    {
        if (!IsCoherentBeginResult(begun, prepared))
        {
            begun.Lease?.Dispose();
            return Result(GovernedLoopEffectAttemptExecutionStatus.EvidenceUnavailable, begun.Attempt, "The durable attempt source returned an incoherent begin result.");
        }
        if (begun.Status == GovernedLoopEffectAttemptStoreStatus.Conflict)
        {
            return Result(GovernedLoopEffectAttemptExecutionStatus.Conflict, begun.Attempt, "The stable effect generation was reused with conflicting authorized content.");
        }
        if (begun.Status == GovernedLoopEffectAttemptStoreStatus.OperationInProgress)
        {
            return Result(GovernedLoopEffectAttemptExecutionStatus.OperationInProgress, begun.Attempt, "Another executor owns the exact effect generation.");
        }
        if (begun.Status == GovernedLoopEffectAttemptStoreStatus.Backpressured)
        {
            return Result(GovernedLoopEffectAttemptExecutionStatus.Backpressured, begun.Attempt, "Durable effect-attempt capacity is exhausted.");
        }
        if (begun.Status is GovernedLoopEffectAttemptStoreStatus.Corrupt or GovernedLoopEffectAttemptStoreStatus.Unavailable
            || begun.Attempt is null)
        {
            begun.Lease?.Dispose();
            return Result(GovernedLoopEffectAttemptExecutionStatus.EvidenceUnavailable, begun.Attempt, "Durable effect-attempt evidence is corrupt or unavailable.");
        }
        if (DoesNotRequireOwner(begun.Attempt.Payload.Phase))
        {
            return ReplayResult(begun.Attempt);
        }
        if (begun.Lease is null)
        {
            return Result(GovernedLoopEffectAttemptExecutionStatus.EvidenceUnavailable, begun.Attempt, "The unfinished effect attempt did not return execution ownership.");
        }
        using var lease = begun.Lease;
        if (begun.Attempt.Payload.Phase == GovernedLoopEffectPhase.OutcomeObserved)
        {
            return await CommitObservedAsync(begun.Attempt, lease, cancellationToken).ConfigureAwait(false);
        }
        if (begun.Attempt.Payload.Phase == GovernedLoopEffectPhase.DispatchBoundaryReached
            || begun.Attempt.Payload.Phase == GovernedLoopEffectPhase.IntentPrepared
                && begun.Attempt.DispatchAuthorityEvidenceHash is not null)
        {
            return begun.Attempt.Payload.Phase == GovernedLoopEffectPhase.DispatchBoundaryReached
                ? await ProbeCrossedAsync(request, preparation.Input, begun.Attempt, lease, cancellationToken).ConfigureAwait(false)
                : await MarkPriorAuthorityAmbiguousAsync(begun.Attempt, begun.Attempt.DispatchAuthorityEvidenceHash, lease, cancellationToken).ConfigureAwait(false);
        }
        return begun.Attempt.Payload.Phase == GovernedLoopEffectPhase.IntentPrepared
            ? await DispatchOwnedAsync(request, preparation, begun.Attempt, lease, cancellationToken).ConfigureAwait(false)
            : Result(GovernedLoopEffectAttemptExecutionStatus.EvidenceUnavailable, begun.Attempt, "The retained effect phase is unsupported.");
    }

    private async Task<GovernedLoopEffectAttemptExecutionResult> DispatchOwnedAsync(
        GovernedLoopEffectAttemptRequest request,
        GovernedActuatorDispatchPreparation preparation,
        GovernedLoopEffectAttempt current,
        IGovernedLoopEffectAttemptLease lease,
        CancellationToken cancellationToken)
    {
        try
        {
            var exact = await ResolveAsync(request, cancellationToken).ConfigureAwait(false);
            if (exact.Status != GovernedActuatorCatalogResolutionStatus.Active
                || exact.Descriptor is null
                || exact.Operation is null
                || !Equals(exact.Descriptor, preparation.Descriptor)
                || !ReferenceEquals(exact.Operation, preparation.Operation))
            {
                return await StopBeforeDispatchAsync(current, lease, "The exact actuator catalog registration changed before authority evaluation.", cancellationToken).ConfigureAwait(false);
            }

            var authorityRequest = new GovernedLoopEffectAuthorityRequest(
                request.AdmissionReceipt,
                request.ExecutionBinding,
                request.GraphArtifact,
                request.NodeId,
                request.NodeAttempt,
                request.IdempotencyOperationId,
                request.CorrelationId,
                GovernedLoopEffectBoundaryKind.ActuatorDispatch,
                preparation.RequiredAuthority,
                [request.CapabilityPin],
                preparation.Evidence.TargetFingerprint);

            var commitCount = 0;
            GovernedActuatorExternalOutcome? observedOutcome = null;
            var authority = await _authority.ExecuteWithDecisionAsync(
                authorityRequest,
                async (decision, authorityToken) =>
                {
                    if (Interlocked.Increment(ref commitCount) != 1
                        || !GovernedLoopEffectAuthorityDecisionMatcher.IsExactMatch(decision, authorityRequest))
                    {
                        throw new GovernedLoopEffectAttemptEvidenceException("The authority boundary supplied an inexact or repeated dispatch decision.");
                    }
                    current = GovernedLoopEffectAttemptContract.AttachDispatchAuthority(current, decision.ContentHash, UtcNowOrThrow(current.Payload.UpdatedAtUtc));
                    var attached = await ExchangeAsync(current.PreviousContentHash!, current, lease, authorityToken).ConfigureAwait(false);
                    if (attached.Status is not (GovernedLoopEffectAttemptStoreStatus.Created or GovernedLoopEffectAttemptStoreStatus.Replayed)
                        || attached.Attempt is null)
                    {
                        throw new GovernedLoopEffectAttemptEvidenceException("Fresh authority could not be attached durably before adapter execution.");
                    }
                    current = attached.Attempt;
                    var boundary = new GovernedActuatorDispatchBoundary(_store, lease, current, preparation.Descriptor, _timeProvider);
                    var invocation = new GovernedActuatorInvocation(
                        preparation.Descriptor,
                        request.EffectId,
                        request.IdempotencyOperationId,
                        request.EffectGeneration,
                        preparation.Input,
                        preparation.Evidence.TargetFingerprint,
                        preparation.Evidence.PreconditionEvidenceHash,
                        preparation.Evidence.BeforeEvidenceId);
                    try
                    {
                        return await preparation.Operation.ExecuteAsync(invocation, boundary, authorityToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        current = boundary.Current;
                        observedOutcome = boundary.ObservedOutcome;
                    }
                },
                cancellationToken).ConfigureAwait(false);

            if (!IsCoherentAuthorityResult(authority, authorityRequest)
                || authority.CommitInvoked != (commitCount == 1))
            {
                return current.DispatchAuthorityEvidenceHash is null
                    ? Result(GovernedLoopEffectAttemptExecutionStatus.EvidenceUnavailable, current, "The authority boundary returned incoherent evidence.")
                    : await RequireReconciliationAsync(current, lease, cancellationToken).ConfigureAwait(false);
            }
            if (!authority.CommitInvoked)
            {
                if (IsPriorDirectSignal(authority))
                {
                    if (!IsCanonicalHash(authority.StoredDecisionContentHash))
                    {
                        return Result(GovernedLoopEffectAttemptExecutionStatus.EvidenceUnavailable, current, "Prior direct-authority evidence was incomplete; no no-dispatch proof was recorded.");
                    }
                    return await MarkPriorAuthorityAmbiguousAsync(current, authority.StoredDecisionContentHash, lease, cancellationToken).ConfigureAwait(false);
                }
                return await StopBeforeDispatchAsync(current, lease, "Current authority stopped the actuator before dispatch.", cancellationToken).ConfigureAwait(false);
            }
            if (authority.Result is null)
            {
                return await RequireReconciliationAsync(current, lease, cancellationToken).ConfigureAwait(false);
            }
            return await FinishAdapterAsync(current, authority.Result, observedOutcome, lease, cancellationToken).ConfigureAwait(false);
        }
        catch (GovernedLoopEffectAttemptEvidenceException)
        {
            return current.Payload.Phase switch
            {
                GovernedLoopEffectPhase.DispatchBoundaryReached => Result(
                    GovernedLoopEffectAttemptExecutionStatus.ReconciliationRequired,
                    current,
                    "The external boundary was durably reached, but reconciliation evidence could not be advanced."),
                GovernedLoopEffectPhase.OutcomeObserved => Result(
                    GovernedLoopEffectAttemptExecutionStatus.EvidenceUnavailable,
                    current,
                    "The conclusive external outcome is durable, but its effect commit could not be advanced."),
                _ => Result(
                    GovernedLoopEffectAttemptExecutionStatus.EvidenceUnavailable,
                    current,
                    "Durable effect-attempt evidence failed closed before external dispatch."),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (current.Payload.Phase == GovernedLoopEffectPhase.IntentPrepared)
            {
                return await StopBeforeDispatchAsync(current, lease, "Cancellation was proved before the actuator boundary.", CancellationToken.None).ConfigureAwait(false);
            }
            return await RequireReconciliationAsync(current, lease, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return current.Payload.Phase == GovernedLoopEffectPhase.IntentPrepared
                ? await StopBeforeDispatchAsync(current, lease, "The adapter failed before its irreversible boundary.", CancellationToken.None).ConfigureAwait(false)
                : await RequireReconciliationAsync(current, lease, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task<GovernedLoopEffectAttemptExecutionResult> FinishAdapterAsync(
        GovernedLoopEffectAttempt current,
        GovernedActuatorAdapterResult adapter,
        GovernedActuatorExternalOutcome? observedOutcome,
        IGovernedLoopEffectAttemptLease lease,
        CancellationToken cancellationToken)
    {
        if (adapter.Status == GovernedActuatorAdapterStatus.DispatchNotStarted
            && current.Payload.Phase == GovernedLoopEffectPhase.IntentPrepared)
        {
            return await StopBeforeDispatchAsync(current, lease, "The server actuator affirmatively proved dispatch did not start.", cancellationToken).ConfigureAwait(false);
        }
        if (adapter.Status != GovernedActuatorAdapterStatus.OutcomeObserved
            || current.Payload.Phase != GovernedLoopEffectPhase.DispatchBoundaryReached
            || observedOutcome is null
            || adapter.Outcome is null
            || !Equals(adapter.Outcome, observedOutcome))
        {
            return await RequireReconciliationAsync(current, lease, cancellationToken).ConfigureAwait(false);
        }

        if (!TryGetUtcNow(current.Payload.UpdatedAtUtc, out var observedAtUtc))
        {
            return Result(
                GovernedLoopEffectAttemptExecutionStatus.ReconciliationRequired,
                current,
                "The external boundary was reached, but trusted time was unavailable for conclusive outcome evidence.");
        }
        var observed = GovernedLoopEffectAttemptContract.Advance(
            current,
            GovernedLoopEffectPhase.OutcomeObserved,
            observedOutcome.Outcome,
            GovernedLoopEffectEvidenceStatus.Complete,
            observedOutcome.OutcomeEvidenceId,
            observedOutcome.AfterEvidenceId,
            observedAtUtc);
        var stored = await ExchangeAsync(current.ContentHash, observed, lease, cancellationToken).ConfigureAwait(false);
        if (stored.Status is not (GovernedLoopEffectAttemptStoreStatus.Created or GovernedLoopEffectAttemptStoreStatus.Replayed)
            || stored.Attempt is null)
        {
            return await RequireReconciliationAsync(current, lease, CancellationToken.None).ConfigureAwait(false);
        }
        return await CommitObservedAsync(stored.Attempt, lease, cancellationToken).ConfigureAwait(false);
    }

    private async Task<GovernedLoopEffectAttemptExecutionResult> ProbeCrossedAsync(
        GovernedLoopEffectAttemptRequest request,
        GovernedActuatorInputEvidence input,
        GovernedLoopEffectAttempt current,
        IGovernedLoopEffectAttemptLease lease,
        CancellationToken cancellationToken)
    {
        try
        {
            var exact = await ResolveAsync(request, cancellationToken).ConfigureAwait(false);
            if (!IsCoherentCatalogResolution(exact, request)
                || exact.Status != GovernedActuatorCatalogResolutionStatus.Active
                || exact.Descriptor is null
                || exact.Operation is not IGovernedActuatorOutcomeProbe probe
                || !string.Equals(exact.Descriptor.ContentHash, current.OperationDescriptorHash, StringComparison.Ordinal))
            {
                return await RequireReconciliationAsync(current, lease, cancellationToken).ConfigureAwait(false);
            }

            var invocation = new GovernedActuatorInvocation(
                exact.Descriptor,
                request.EffectId,
                request.IdempotencyOperationId,
                request.EffectGeneration,
                input,
                current.TargetFingerprint,
                current.PreconditionEvidenceHash,
                current.BeforeEvidenceId);
            var result = await probe.ProbeAsync(invocation, cancellationToken).ConfigureAwait(false);
            if (result.Posture != GovernedActuatorProbePosture.OutcomeObserved
                || result.Outcome is null)
            {
                return await RequireReconciliationAsync(current, lease, cancellationToken).ConfigureAwait(false);
            }
            if (!TryGetUtcNow(current.Payload.UpdatedAtUtc, out var observedAtUtc))
            {
                return Result(
                    GovernedLoopEffectAttemptExecutionStatus.ReconciliationRequired,
                    current,
                    "The external outcome was proved by retained evidence, but trusted time was unavailable to adopt it.");
            }
            var observed = GovernedLoopEffectAttemptContract.Advance(
                current,
                GovernedLoopEffectPhase.OutcomeObserved,
                result.Outcome.Outcome,
                GovernedLoopEffectEvidenceStatus.Complete,
                result.Outcome.OutcomeEvidenceId,
                result.Outcome.AfterEvidenceId,
                observedAtUtc);
            var stored = await ExchangeAsync(current.ContentHash, observed, lease, cancellationToken).ConfigureAwait(false);
            if (stored.Status is not (GovernedLoopEffectAttemptStoreStatus.Created or GovernedLoopEffectAttemptStoreStatus.Replayed)
                || stored.Attempt is null)
            {
                return await RequireReconciliationAsync(current, lease, CancellationToken.None).ConfigureAwait(false);
            }
            return await CommitObservedAsync(stored.Attempt, lease, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return await RequireReconciliationAsync(current, lease, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return await RequireReconciliationAsync(current, lease, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task<GovernedLoopEffectAttemptExecutionResult> CommitObservedAsync(
        GovernedLoopEffectAttempt current,
        IGovernedLoopEffectAttemptLease lease,
        CancellationToken cancellationToken)
    {
        if (!TryGetUtcNow(current.Payload.UpdatedAtUtc, out var committedAtUtc))
        {
            return Result(
                GovernedLoopEffectAttemptExecutionStatus.EvidenceUnavailable,
                current,
                "The conclusive external outcome is durable, but trusted time was unavailable for effect commit.");
        }
        var committed = GovernedLoopEffectAttemptContract.Advance(
            current,
            GovernedLoopEffectPhase.Committed,
            current.Payload.Outcome,
            current.Payload.EvidenceStatus,
            current.Payload.OutcomeEvidenceId,
            current.AfterEvidenceId,
            committedAtUtc);
        var stored = await ExchangeAsync(current.ContentHash, committed, lease, cancellationToken).ConfigureAwait(false);
        return stored.Status is GovernedLoopEffectAttemptStoreStatus.Created or GovernedLoopEffectAttemptStoreStatus.Replayed
            && stored.Attempt?.Payload.Phase == GovernedLoopEffectPhase.Committed
            ? Result(GovernedLoopEffectAttemptExecutionStatus.Committed, stored.Attempt, "The conclusive effect outcome was committed from durable evidence.")
            : Result(GovernedLoopEffectAttemptExecutionStatus.EvidenceUnavailable, stored.Attempt ?? current, "The conclusive outcome is durable but its effect commit remains incomplete.");
    }

    private async Task<GovernedLoopEffectAttemptExecutionResult> StopBeforeDispatchAsync(
        GovernedLoopEffectAttempt current,
        IGovernedLoopEffectAttemptLease lease,
        string detail,
        CancellationToken cancellationToken)
    {
        if (current.Payload.Phase != GovernedLoopEffectPhase.IntentPrepared)
        {
            return await RequireReconciliationAsync(current, lease, cancellationToken).ConfigureAwait(false);
        }
        if (!TryGetUtcNow(current.Payload.UpdatedAtUtc, out var stoppedAtUtc))
        {
            return Result(GovernedLoopEffectAttemptExecutionStatus.EvidenceUnavailable, current, "Trusted time was unavailable for pre-dispatch stop evidence.");
        }
        var stopped = GovernedLoopEffectAttemptContract.Advance(
            current,
            GovernedLoopEffectPhase.DispatchNotStarted,
            GovernedLoopEffectOutcome.None,
            GovernedLoopEffectEvidenceStatus.Complete,
            null,
            null,
            stoppedAtUtc);
        var stored = await ExchangeAsync(current.ContentHash, stopped, lease, cancellationToken).ConfigureAwait(false);
        return stored.Status is GovernedLoopEffectAttemptStoreStatus.Created or GovernedLoopEffectAttemptStoreStatus.Replayed
            ? Result(GovernedLoopEffectAttemptExecutionStatus.DispatchNotStarted, stored.Attempt, IsServiceDetail(detail) ? detail : "Dispatch was affirmatively proved not to have started.")
            : Result(GovernedLoopEffectAttemptExecutionStatus.EvidenceUnavailable, stored.Attempt ?? current, "Pre-dispatch stop evidence could not be retained.");
    }

    private async Task<GovernedLoopEffectAttemptExecutionResult> MarkPriorAuthorityAmbiguousAsync(
        GovernedLoopEffectAttempt current,
        string? evidenceHash,
        IGovernedLoopEffectAttemptLease lease,
        CancellationToken cancellationToken)
    {
        if (current.DispatchAuthorityEvidenceHash is null)
        {
            if (!IsCanonicalHash(evidenceHash))
            {
                return Result(GovernedLoopEffectAttemptExecutionStatus.EvidenceUnavailable, current, "Prior direct-authority evidence was incomplete; no no-dispatch proof was recorded.");
            }
            if (!TryGetUtcNow(current.Payload.UpdatedAtUtc, out var authorityAtUtc))
            {
                return Result(GovernedLoopEffectAttemptExecutionStatus.EvidenceUnavailable, current, "Trusted time was unavailable for prior-authority ambiguity evidence.");
            }
            current = GovernedLoopEffectAttemptContract.AttachDispatchAuthority(current, evidenceHash!, authorityAtUtc);
            var attached = await ExchangeAsync(current.PreviousContentHash!, current, lease, cancellationToken).ConfigureAwait(false);
            if (attached.Status is not (GovernedLoopEffectAttemptStoreStatus.Created or GovernedLoopEffectAttemptStoreStatus.Replayed)
                || attached.Attempt is null)
            {
                return Result(GovernedLoopEffectAttemptExecutionStatus.EvidenceUnavailable, current, "Prior direct-authority ambiguity could not be retained.");
            }
            current = attached.Attempt;
        }
        if (current.Payload.Phase == GovernedLoopEffectPhase.IntentPrepared)
        {
            if (!TryGetUtcNow(current.Payload.UpdatedAtUtc, out var crossedAtUtc))
            {
                return Result(GovernedLoopEffectAttemptExecutionStatus.EvidenceUnavailable, current, "Prior direct authority exists, but trusted time was unavailable for ambiguity evidence.");
            }
            var crossed = GovernedLoopEffectAttemptContract.Advance(
                current,
                GovernedLoopEffectPhase.DispatchBoundaryReached,
                GovernedLoopEffectOutcome.OutcomeUnknown,
                GovernedLoopEffectEvidenceStatus.Incomplete,
                null,
                null,
                crossedAtUtc);
            var stored = await ExchangeAsync(current.ContentHash, crossed, lease, cancellationToken).ConfigureAwait(false);
            if (stored.Status is not (GovernedLoopEffectAttemptStoreStatus.Created or GovernedLoopEffectAttemptStoreStatus.Replayed)
                || stored.Attempt is null)
            {
                return Result(GovernedLoopEffectAttemptExecutionStatus.EvidenceUnavailable, current, "Prior direct-authority ambiguity could not be retained.");
            }
            current = stored.Attempt;
        }
        return await RequireReconciliationAsync(current, lease, cancellationToken).ConfigureAwait(false);
    }

    private async Task<GovernedLoopEffectAttemptExecutionResult> RequireReconciliationAsync(
        GovernedLoopEffectAttempt current,
        IGovernedLoopEffectAttemptLease lease,
        CancellationToken cancellationToken)
    {
        if (current.Payload.Phase == GovernedLoopEffectPhase.ReconciliationRequired)
        {
            return Result(GovernedLoopEffectAttemptExecutionStatus.ReconciliationRequired, current, "The external outcome requires explicit reconciliation; redispatch is forbidden.");
        }
        if (current.Payload.Phase == GovernedLoopEffectPhase.IntentPrepared)
        {
            if (current.DispatchAuthorityEvidenceHash is null)
            {
                return Result(GovernedLoopEffectAttemptExecutionStatus.EvidenceUnavailable, current, "The authority evidence needed to prove possible dispatch is unavailable.");
            }
            if (!TryGetUtcNow(current.Payload.UpdatedAtUtc, out var crossedAtUtc))
            {
                return Result(GovernedLoopEffectAttemptExecutionStatus.EvidenceUnavailable, current, "Possible dispatch is proved, but trusted time was unavailable for boundary evidence.");
            }
            var crossed = GovernedLoopEffectAttemptContract.Advance(
                current,
                GovernedLoopEffectPhase.DispatchBoundaryReached,
                GovernedLoopEffectOutcome.OutcomeUnknown,
                GovernedLoopEffectEvidenceStatus.Incomplete,
                null,
                null,
                crossedAtUtc);
            var crossedStore = await ExchangeAsync(current.ContentHash, crossed, lease, cancellationToken).ConfigureAwait(false);
            if (crossedStore.Status is not (GovernedLoopEffectAttemptStoreStatus.Created or GovernedLoopEffectAttemptStoreStatus.Replayed)
                || crossedStore.Attempt is null)
            {
                return Result(GovernedLoopEffectAttemptExecutionStatus.EvidenceUnavailable, current, "Ambiguous dispatch-boundary evidence could not be retained.");
            }
            current = crossedStore.Attempt;
        }
        if (current.Payload.Phase != GovernedLoopEffectPhase.DispatchBoundaryReached)
        {
            return Result(GovernedLoopEffectAttemptExecutionStatus.EvidenceUnavailable, current, "The retained attempt cannot safely enter reconciliation-required posture.");
        }
        if (!TryGetUtcNow(current.Payload.UpdatedAtUtc, out var reconciliationAtUtc))
        {
            return Result(
                GovernedLoopEffectAttemptExecutionStatus.ReconciliationRequired,
                current,
                "The external boundary was durably reached, but trusted time was unavailable for terminal reconciliation evidence.");
        }
        var reconciliation = GovernedLoopEffectAttemptContract.Advance(
            current,
            GovernedLoopEffectPhase.ReconciliationRequired,
            GovernedLoopEffectOutcome.OutcomeUnknown,
            GovernedLoopEffectEvidenceStatus.Incomplete,
            null,
            null,
            reconciliationAtUtc);
        var stored = await ExchangeAsync(current.ContentHash, reconciliation, lease, cancellationToken).ConfigureAwait(false);
        return stored.Status is GovernedLoopEffectAttemptStoreStatus.Created or GovernedLoopEffectAttemptStoreStatus.Replayed
            ? Result(GovernedLoopEffectAttemptExecutionStatus.ReconciliationRequired, stored.Attempt, "The external outcome requires explicit reconciliation; redispatch is forbidden.")
            : Result(GovernedLoopEffectAttemptExecutionStatus.EvidenceUnavailable, stored.Attempt ?? current, "Reconciliation-required evidence could not be retained.");
    }

    private async Task<(GovernedActuatorDispatchPreparation? Preparation, GovernedLoopEffectAttemptExecutionResult? Failure)> PrepareDispatchAsync(
        GovernedLoopEffectAttemptRequest request,
        GovernedActuatorInputEvidence canonicalInput,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveAsync(request, cancellationToken).ConfigureAwait(false);
        if (!IsCoherentCatalogResolution(resolved, request))
        {
            return (null, Result(GovernedLoopEffectAttemptExecutionStatus.CatalogUnavailable, null, "The actuator catalog returned incoherent resolution evidence."));
        }
        if (resolved.Status != GovernedActuatorCatalogResolutionStatus.Active)
        {
            return (null, Result(
                resolved.Status == GovernedActuatorCatalogResolutionStatus.InvalidRequest
                    ? GovernedLoopEffectAttemptExecutionStatus.InvalidRequest
                    : GovernedLoopEffectAttemptExecutionStatus.CatalogUnavailable,
                null,
                "The exact admitted actuator operation is not currently available."));
        }

        if (!GovernedActuatorInputContract.TryCreate(canonicalInput.CanonicalJson, resolved.Capability!.InputSchema, out var schemaInput, out var inputFailure)
            || !string.Equals(schemaInput!.Fingerprint, canonicalInput.Fingerprint, StringComparison.Ordinal))
        {
            return (null, Result(GovernedLoopEffectAttemptExecutionStatus.InvalidRequest, null, inputFailure ?? "The structured actuator input is invalid."));
        }

        string? adapterFailure;
        GovernedActuatorPreparationEvidence? evidence;
        try
        {
            adapterFailure = resolved.Operation!.ValidateInput(schemaInput);
            if (adapterFailure is not null)
            {
                return (null, Result(GovernedLoopEffectAttemptExecutionStatus.InvalidRequest, null, "The server actuator rejected the structured input."));
            }

            evidence = await resolved.Operation.PrepareAsync(schemaInput, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return (null, Result(GovernedLoopEffectAttemptExecutionStatus.InvalidRequest, null, "The server actuator preparation failed closed."));
        }
        if (!IsCoherentPreparation(evidence, resolved.Descriptor!))
        {
            return (null, Result(GovernedLoopEffectAttemptExecutionStatus.InvalidRequest, null, "The server actuator preparation evidence is missing, unsafe, or does not match the requested target."));
        }

        var requiredAuthority = RequiredAuthority(resolved.Capability, request.CapabilityPin.DescriptorIdentity);
        if (!AuthorityCeilingSubset.IsEqual(requiredAuthority, request.RequiredAuthority))
        {
            return (null, Result(GovernedLoopEffectAttemptExecutionStatus.InvalidRequest, null, "The caller-supplied authority does not exactly match the server-derived capability requirement."));
        }
        if (resolved.Descriptor!.Approval == GovernedActuatorApprovalPosture.GovernedApprovalRequired
            || !resolved.Descriptor.UnattendedEligible)
        {
            return (null, Result(GovernedLoopEffectAttemptExecutionStatus.ApprovalRequired, null, "A separate governed approval proof is required before this operation may dispatch."));
        }

        return (new GovernedActuatorDispatchPreparation(
            resolved.Capability,
            resolved.Descriptor,
            resolved.Operation!,
            schemaInput,
            evidence!,
            requiredAuthority), null);
    }

    private static AuthorityCeiling RequiredAuthority(
        CapabilityDescriptor capability,
        EmbodySense.Core.Common.Capabilities.CapabilityDescriptorIdentity identity)
    {
        var externallyVisible = capability.SideEffectClass is CapabilitySideEffectClass.ExternalReversible or CapabilitySideEffectClass.Irreversible;
        return new AuthorityCeiling(
            [identity],
            capability.Requirements.DataClasses,
            1,
            capability.SideEffectClass,
            false,
            externallyVisible,
            capability.SideEffectClass == CapabilitySideEffectClass.Irreversible);
    }

    private static bool IsCoherentPreparation(
        GovernedActuatorPreparationEvidence? evidence,
        GovernedActuatorOperationDescriptor descriptor)
        => evidence is not null
            && IsCanonicalHash(evidence.TargetFingerprint)
            && (evidence.PreconditionEvidenceHash is null || IsCanonicalHash(evidence.PreconditionEvidenceHash))
            && (evidence.BeforeEvidenceId is null || IsEvidenceReference(evidence.BeforeEvidenceId))
            && descriptor.RequiresOptimisticPrecondition == (evidence.PreconditionEvidenceHash is not null)
            && descriptor.RequiresBeforeEvidence == (evidence.BeforeEvidenceId is not null);

    private static bool IsCoherentCatalogResolution(
        GovernedActuatorCatalogResolutionResult resolution,
        GovernedLoopEffectAttemptRequest request)
    {
        if (!Enum.IsDefined(resolution.Status) || resolution.Status == GovernedActuatorCatalogResolutionStatus.Unknown)
        {
            return false;
        }
        if (resolution.Status != GovernedActuatorCatalogResolutionStatus.Active)
        {
            return resolution.Operation is null;
        }
        try
        {
            return resolution.Capability is not null
                && resolution.Descriptor is not null
                && resolution.Operation is not null
                && CapabilityDescriptorValidator.Validate(resolution.Capability).IsValid
                && resolution.Capability.Kind == EmbodySense.Core.Common.Capabilities.Models.CapabilityKind.Actuator
                && EmbodySense.Core.Common.Capabilities.CapabilityDescriptorIdentity.TryCreate(resolution.Capability, out var identity, out _)
                && Equals(identity, request.CapabilityPin.DescriptorIdentity)
                && Equals(resolution.Capability.Implementation, request.CapabilityPin.Implementation)
                && Equals(resolution.Descriptor.Capability, request.CapabilityPin.DescriptorIdentity)
                && Equals(resolution.Descriptor.Implementation, request.CapabilityPin.Implementation)
                && string.Equals(resolution.Descriptor.OperationId, request.ActuatorOperationId, StringComparison.Ordinal)
                && GovernedActuatorOperationContract.Validate(resolution.Descriptor) is null
                && Equals(resolution.Operation.Descriptor, resolution.Descriptor);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return false;
        }
    }

    private static bool IsCoherentRetainedIdentity(
        GovernedLoopEffectAttempt? attempt,
        GovernedLoopEffectAttemptRequest request,
        GovernedActuatorInputEvidence input,
        out bool storeEvidenceInvalid)
    {
        storeEvidenceInvalid = true;
        try
        {
            if (GovernedLoopEffectAttemptContract.Validate(attempt) is not null
                || !string.Equals(attempt!.Payload.OperationId, request.IdempotencyOperationId, StringComparison.Ordinal)
                || attempt.Payload.EffectGeneration != request.EffectGeneration)
            {
                return false;
            }
            storeEvidenceInvalid = false;
            return Equals(attempt.Binding, request.ExecutionBinding)
                && string.Equals(attempt.NodeId, request.NodeId, StringComparison.Ordinal)
                && attempt.NodeAttempt == request.NodeAttempt
                && Equals(attempt.Capability, request.CapabilityPin.DescriptorIdentity)
                && Equals(attempt.Implementation, request.CapabilityPin.Implementation)
                && string.Equals(attempt.ActuatorOperationId, request.ActuatorOperationId, StringComparison.Ordinal)
                && string.Equals(attempt.Payload.EffectId, request.EffectId, StringComparison.Ordinal)
                && string.Equals(attempt.InputFingerprint, input.Fingerprint, StringComparison.Ordinal)
                && string.Equals(attempt.AdmissionAuthorityEvidenceHash, request.AdmissionReceipt.ContentHash, StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            storeEvidenceInvalid = true;
            return false;
        }
    }

    private static bool IsCoherentBeginResult(
        GovernedLoopEffectAttemptStoreResult result,
        GovernedLoopEffectAttempt prepared)
    {
        if (!Enum.IsDefined(result.Status) || result.Status == GovernedLoopEffectAttemptStoreStatus.NotFound)
        {
            return false;
        }
        if (result.Status is GovernedLoopEffectAttemptStoreStatus.Created or GovernedLoopEffectAttemptStoreStatus.Replayed)
        {
            return GovernedLoopEffectAttemptContract.Validate(result.Attempt) is null
                && GovernedLoopEffectAttemptContract.HasSameIntent(prepared, result.Attempt!)
                && DoesNotRequireOwner(result.Attempt!.Payload.Phase) == (result.Lease is null);
        }
        return result.Lease is null
            && (result.Attempt is null
                || GovernedLoopEffectAttemptContract.Validate(result.Attempt) is null
                    && string.Equals(result.Attempt.Payload.OperationId, prepared.Payload.OperationId, StringComparison.Ordinal)
                    && result.Attempt.Payload.EffectGeneration == prepared.Payload.EffectGeneration);
    }

    private static bool DoesNotRequireOwner(GovernedLoopEffectPhase phase)
        => phase is GovernedLoopEffectPhase.DispatchNotStarted
            or GovernedLoopEffectPhase.Committed
            or GovernedLoopEffectPhase.ReconciliationRequired
            or GovernedLoopEffectPhase.Reconciled;

    private static bool IsPriorDirectSignal(GovernedLoopEffectAuthorityExecutionResult<GovernedActuatorAdapterResult> authority)
        => authority.Status == GovernedLoopEffectAuthorityExecutionStatus.EvidenceRejected
            && authority.EvidenceStatus == GovernedLoopEffectAuthorityEvidenceStoreStatus.AlreadyPresent
                && authority.Decision?.Disposition == GovernedLoopEffectAuthorityDisposition.Pause
                && authority.Decision.Reason == GovernedLoopEffectAuthorityReason.EvidenceAmbiguous;

    private static bool IsCoherentAuthorityResult(
        GovernedLoopEffectAuthorityExecutionResult<GovernedActuatorAdapterResult>? authority,
        GovernedLoopEffectAuthorityRequest request)
    {
        if (authority is null || !Enum.IsDefined(authority.Status) || !Enum.IsDefined(authority.EvidenceStatus))
        {
            return false;
        }
        if (authority.Decision is not null
            && !GovernedLoopEffectAuthorityDecisionMatcher.IsExactMatch(authority.Decision, request))
        {
            return false;
        }
        if (authority.CommitInvoked)
        {
            return authority.Status == GovernedLoopEffectAuthorityExecutionStatus.Decided
                && authority.EvidenceStatus == GovernedLoopEffectAuthorityEvidenceStoreStatus.Appended
                && authority.Result is not null
                && authority.Decision?.Disposition == GovernedLoopEffectAuthorityDisposition.Direct
                && string.Equals(authority.StoredDecisionContentHash, authority.Decision.ContentHash, StringComparison.Ordinal);
        }
        if (authority.Result is not null)
        {
            return false;
        }
        if (IsPriorDirectSignal(authority))
        {
            return true;
        }
        return authority.Decision is null
            ? authority.StoredDecisionContentHash is null
            : authority.Decision.Disposition is GovernedLoopEffectAuthorityDisposition.Deny or GovernedLoopEffectAuthorityDisposition.Pause
                && (authority.StoredDecisionContentHash is null
                    || string.Equals(authority.StoredDecisionContentHash, authority.Decision.ContentHash, StringComparison.Ordinal));
    }

    private static GovernedLoopEffectAttemptExecutionResult ReplayResult(GovernedLoopEffectAttempt attempt)
        => attempt.Payload.Phase switch
        {
            GovernedLoopEffectPhase.Committed => Result(GovernedLoopEffectAttemptExecutionStatus.Replayed, attempt, "The exact committed effect evidence was replayed without dispatch."),
            GovernedLoopEffectPhase.DispatchNotStarted => Result(GovernedLoopEffectAttemptExecutionStatus.DispatchNotStarted, attempt, "The exact proved pre-dispatch stop was replayed without dispatch."),
            GovernedLoopEffectPhase.ReconciliationRequired => Result(GovernedLoopEffectAttemptExecutionStatus.ReconciliationRequired, attempt, "The exact ambiguity evidence was replayed; redispatch is forbidden."),
            _ => Result(GovernedLoopEffectAttemptExecutionStatus.EvidenceUnavailable, attempt, "The retained attempt posture is unsupported for terminal replay."),
        };

    private async Task<GovernedActuatorCatalogResolutionResult> ResolveAsync(
        GovernedLoopEffectAttemptRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _catalog.ResolveAsync(request.CapabilityPin, request.ActuatorOperationId, cancellationToken).ConfigureAwait(false)
                ?? new GovernedActuatorCatalogResolutionResult(GovernedActuatorCatalogResolutionStatus.CatalogUnavailable, null, null, null, "The actuator catalog returned no resolution.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new GovernedActuatorCatalogResolutionResult(GovernedActuatorCatalogResolutionStatus.CatalogUnavailable, null, null, null, "The actuator catalog was unavailable.");
        }
    }

    private async Task<GovernedLoopEffectAttemptStoreResult> ResumeAsync(
        string operationId,
        long effectGeneration,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _store.ResumeAsync(operationId, effectGeneration, cancellationToken).ConfigureAwait(false)
                ?? new GovernedLoopEffectAttemptStoreResult(GovernedLoopEffectAttemptStoreStatus.Unavailable);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new GovernedLoopEffectAttemptStoreResult(GovernedLoopEffectAttemptStoreStatus.Unavailable);
        }
    }

    private async Task<GovernedLoopEffectAttemptStoreResult> BeginAsync(
        GovernedActuatorDispatchPreparation preparation,
        GovernedLoopEffectAttempt prepared,
        CancellationToken cancellationToken)
    {
        try
        {
            if (preparation.Operation is not IGovernedActuatorPreparationValidator validator)
            {
                return await _store.BeginAsync(prepared, cancellationToken).ConfigureAwait(false)
                    ?? new GovernedLoopEffectAttemptStoreResult(GovernedLoopEffectAttemptStoreStatus.Unavailable);
            }
            if (_store is not IGovernedLoopEffectAttemptPreparationClaimStore claimStore)
            {
                return new GovernedLoopEffectAttemptStoreResult(GovernedLoopEffectAttemptStoreStatus.Unavailable);
            }
            return await claimStore.BeginWithPreparationClaimAsync(
                    prepared,
                    token => validator.IsPreparationCurrentAsync(preparation.Input, preparation.Evidence, token),
                    cancellationToken)
                .ConfigureAwait(false)
                ?? new GovernedLoopEffectAttemptStoreResult(GovernedLoopEffectAttemptStoreStatus.Unavailable);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new GovernedLoopEffectAttemptStoreResult(GovernedLoopEffectAttemptStoreStatus.Unavailable);
        }
    }

    private async Task<GovernedLoopEffectAttemptStoreResult> ExchangeAsync(
        string expected,
        GovernedLoopEffectAttempt replacement,
        IGovernedLoopEffectAttemptLease lease,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _store.CompareExchangeAsync(expected, replacement, lease, cancellationToken).ConfigureAwait(false);
            if (result is null
                || !Enum.IsDefined(result.Status)
                || result.Lease is not null
                || result.Status is GovernedLoopEffectAttemptStoreStatus.Created or GovernedLoopEffectAttemptStoreStatus.Replayed
                    && (GovernedLoopEffectAttemptContract.Validate(result.Attempt) is not null
                        || !string.Equals(result.Attempt!.ContentHash, replacement.ContentHash, StringComparison.Ordinal))
                || result.Status is not (GovernedLoopEffectAttemptStoreStatus.Created or GovernedLoopEffectAttemptStoreStatus.Replayed)
                    && result.Attempt is not null
                    && (GovernedLoopEffectAttemptContract.Validate(result.Attempt) is not null
                        || !string.Equals(result.Attempt.Payload.OperationId, replacement.Payload.OperationId, StringComparison.Ordinal)
                        || result.Attempt.Payload.EffectGeneration != replacement.Payload.EffectGeneration))
            {
                return new GovernedLoopEffectAttemptStoreResult(GovernedLoopEffectAttemptStoreStatus.Unavailable);
            }
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new GovernedLoopEffectAttemptStoreResult(GovernedLoopEffectAttemptStoreStatus.Unavailable);
        }
    }

    private static bool IsStructurallyValid(GovernedLoopEffectAttemptRequest? request)
    {
        try
        {
            return IsStructurallyValidCore(request);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return false;
        }
    }

    private static bool IsStructurallyValidCore(GovernedLoopEffectAttemptRequest? request)
    {
        if (request?.AdmissionReceipt is null
            || request.ExecutionBinding is null
            || request.GraphArtifact is null
            || request.CapabilityPin?.DescriptorIdentity is null
            || request.RequiredAuthority is null
            || !EmbodySense.Core.Common.Authority.AuthorityProfileValidator.ValidateCeiling(request.RequiredAuthority).IsValid
            || !GovernedLoopAdmissionValidator.Validate(request.AdmissionReceipt).IsValid
            || !Equals(request.ExecutionBinding, request.AdmissionReceipt.Evidence.Binding)
            || !request.AdmissionReceipt.Evidence.CapabilityAdmission.Pins.Contains(request.CapabilityPin)
            || !request.RequiredAuthority.Capabilities.Contains(request.CapabilityPin.DescriptorIdentity)
            || request.RequiredAuthority.Capabilities.Count != 1
            || request.GraphArtifact.Graph.Nodes.SingleOrDefault(node => string.Equals(node.Id, request.NodeId, StringComparison.Ordinal)) is not { Descriptor.Kind: Common.Loops.Models.Custom.Graph.GovernedLoopNodeKind.Action } node
            || !node.AuthorityCeiling.CapabilityIds.Contains(request.CapabilityPin.DescriptorIdentity.Id.Value, StringComparer.Ordinal)
            || request.NodeAttempt is < 1 or > GovernedLoopExecutionLimits.MaxNodeAttempt
            || request.EffectGeneration is < 1 or > GovernedLoopExecutionLimits.MaxVersion
            || !GovernedActuatorOperationContract.IsOperationId(request.ActuatorOperationId)
            || !IsIdentifier(request.EffectId)
            || !IsIdentifier(request.IdempotencyOperationId)
            || !IsIdentifier(request.CorrelationId))
        {
            return false;
        }

        try
        {
            return string.Equals(Common.Loops.Revisions.GovernedLoopGraphRevisionContractHash.ComputeArtifactHash(request.GraphArtifact), request.GraphArtifact.ArtifactHash, StringComparison.Ordinal)
                && string.Equals(request.GraphArtifact.ArtifactHash, request.AdmissionReceipt.Intent.GraphArtifactHash, StringComparison.Ordinal)
                && Equals(request.GraphArtifact.RevisionArtifact.Revision, request.ExecutionBinding.Revision);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return false;
        }
    }

    private bool TryGetUtcNow(out DateTimeOffset utc)
    {
        try
        {
            utc = _timeProvider.GetUtcNow();
            return utc != default && utc.Offset == TimeSpan.Zero;
        }
        catch (Exception)
        {
            utc = default;
            return false;
        }
    }

    private bool TryGetUtcNow(DateTimeOffset minimum, out DateTimeOffset utc)
        => TryGetUtcNow(out utc) && utc >= minimum;

    private DateTimeOffset UtcNowOrThrow(DateTimeOffset minimum)
        => TryGetUtcNow(minimum, out var utc) ? utc : throw new GovernedLoopEffectAttemptEvidenceException("Trusted UTC time became unavailable or moved backwards.");

    private static bool IsServiceDetail(string? detail)
        => detail is null || detail.Length is > 0 and <= GovernedLoopEffectAttemptContractLimits.MaxDetailCharacters
            && detail.All(character => character is >= ' ' and <= '~' or '\n' or '\r' or '\t');

    private static bool IsIdentifier(string? value)
        => !string.IsNullOrEmpty(value)
            && value.Length <= GovernedLoopExecutionLimits.MaxIdentifierCharacters
            && value[0] is >= 'a' and <= 'z' or >= '0' and <= '9'
            && value[^1] is >= 'a' and <= 'z' or >= '0' and <= '9'
            && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '.');

    private static bool IsEvidenceReference(string? value)
        => CustomLoopArtifactIdentifier.IsValid(value, GovernedLoopExecutionLimits.MaxEvidenceReferenceCharacters);

    private static bool IsCanonicalHash(string? value)
        => value is { Length: GovernedLoopExecutionLimits.Sha256HexCharacters }
            && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static GovernedLoopEffectAttemptExecutionResult Result(
        GovernedLoopEffectAttemptExecutionStatus status,
        GovernedLoopEffectAttempt? attempt,
        string detail)
        => new(status, attempt, detail.Length <= GovernedLoopEffectAttemptContractLimits.MaxDetailCharacters ? detail : detail[..GovernedLoopEffectAttemptContractLimits.MaxDetailCharacters]);

}
