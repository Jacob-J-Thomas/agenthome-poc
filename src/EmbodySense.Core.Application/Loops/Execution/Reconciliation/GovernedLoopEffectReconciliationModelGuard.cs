using EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Effects;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Core.Application.Loops.Execution.Reconciliation;

internal static class GovernedLoopEffectReconciliationModelGuard
{
    internal static TStatus RequireDefinedStatus<TStatus>(TStatus status, string parameterName)
        where TStatus : struct, Enum
        => Enum.IsDefined(status) ? status : throw new ArgumentOutOfRangeException(parameterName, "A reconciliation result status must be defined.");

    internal static string RequireIdentifier(string? value, string parameterName)
        => GovernedLoopEffectReconciliationProjectionGuard.RequireIdentifier(value, parameterName);

    internal static string RequireSha256(string? value, string parameterName)
        => GovernedLoopEffectReconciliationProjectionGuard.RequireSha256(value, parameterName);

    internal static long? RequireExpectedCaseVersion(
        long? version,
        string? hash,
        GovernedLoopEffectReconciliationCase? replacement,
        string parameterName)
    {
        if ((version is null) != (hash is null))
        {
            throw new ArgumentException("Expected case version and content hash must either both be absent for create or both identify an existing case.", parameterName);
        }

        var exactReplacement = CopyRequiredCase(replacement, nameof(replacement));
        if (version is null)
        {
            if (exactReplacement.CaseVersion != 1 || exactReplacement.PreviousContentHash is not null)
            {
                throw new ArgumentException("A create mutation must store the first case version without a predecessor hash.", parameterName);
            }

            return null;
        }

        var exactVersion = GovernedLoopEffectReconciliationProjectionGuard.RequirePositiveVersion(version.Value, parameterName);
        if (exactVersion == long.MaxValue
            || exactReplacement.CaseVersion != exactVersion + 1
            || !string.Equals(hash, exactReplacement.PreviousContentHash, StringComparison.Ordinal))
        {
            throw new ArgumentException("An update mutation must store the direct case-version and hash-chain successor of its expected case.", parameterName);
        }

        return exactVersion;
    }

    internal static string? RequireExpectedCaseHash(long? version, string? hash, string parameterName)
    {
        if ((version is null) != (hash is null))
        {
            throw new ArgumentException("Expected case version and content hash must either both be absent for create or both identify an existing case.", parameterName);
        }

        return hash is null ? null : RequireSha256(hash, parameterName);
    }

    internal static GovernedLoopEffectReconciliationCaseReference CopyRequiredReference(GovernedLoopEffectReconciliationCaseReference? value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        return new GovernedLoopEffectReconciliationCaseReference(value.CaseId, value.CaseVersion, value.ContentHash, value.BindingHash);
    }

    internal static GovernedLoopEffectReconciliationBinding CopyRequiredBinding(GovernedLoopEffectReconciliationBinding? value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (!GovernedLoopEffectReconciliationContractValidator.Validate(value).IsValid)
        {
            throw new ArgumentException("A reconciliation binding must be complete, canonical, and hash-valid.", parameterName);
        }

        return GovernedLoopEffectReconciliationContractCopy.Copy(value);
    }

    internal static GovernedLoopEffectReconciliationBinding CopyBoundBinding(
        GovernedLoopEffectReconciliationCaseReference? caseReference,
        GovernedLoopEffectReconciliationBinding? binding,
        string parameterName)
    {
        var exactCase = CopyRequiredReference(caseReference, nameof(caseReference));
        var exactBinding = CopyRequiredBinding(binding, parameterName);
        if (!string.Equals(exactCase.BindingHash, exactBinding.ContentHash, StringComparison.Ordinal))
        {
            throw new ArgumentException("The exact case reference and reconciliation binding must name the same binding hash.", parameterName);
        }

        return exactBinding;
    }

    internal static GovernedLoopEffectReconciliationContractMetadata CopyRequiredMetadata(GovernedLoopEffectReconciliationContractMetadata? value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (!GovernedLoopEffectReconciliationContractValidator.Validate(value).IsValid)
        {
            throw new ArgumentException("Reconciliation contract metadata must be complete, canonical, and hash-valid.", parameterName);
        }

        return GovernedLoopEffectReconciliationContractCopy.Copy(value);
    }

    internal static GovernedLoopEffectReconciliationCase CopyRequiredCase(GovernedLoopEffectReconciliationCase? value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (!GovernedLoopEffectReconciliationContractValidator.Validate(value).IsValid)
        {
            throw new ArgumentException("A reconciliation case must be complete, canonical, and hash-valid.", parameterName);
        }

        return GovernedLoopEffectReconciliationContractCopy.Copy(value);
    }

    internal static GovernedActuatorInputEvidence CopyRequiredInput(GovernedActuatorInputEvidence? value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (!GovernedActuatorInputContract.TryCanonicalize(value.CanonicalJson, out var canonical, out _)
            || canonical is null
            || !Equals(value, canonical))
        {
            throw new ArgumentException("Reconciliation input must be exact bounded canonical actuator input.", parameterName);
        }

        return GovernedLoopEffectReconciliationApplicationCopy.Copy(value)!;
    }

    internal static GovernedLoopEffectAttempt CopyProbeEffect(
        GovernedLoopEffectAttempt? value,
        GovernedLoopEffectReconciliationBinding binding,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);

        var exact = CopyOptionalAttempt(value, parameterName)!;
        if (exact.Payload.Phase != GovernedLoopEffectPhase.ReconciliationRequired
            || !string.Equals(exact.ContentHash, binding.CurrentAttemptHash, StringComparison.Ordinal)
            || !Equals(exact.Binding, binding.Execution)
            || !string.Equals(exact.NodeId, binding.NodeId, StringComparison.Ordinal)
            || exact.NodeAttempt != binding.NodeAttempt
            || !string.Equals(exact.Payload.EffectId, binding.EffectId, StringComparison.Ordinal)
            || !string.Equals(exact.Payload.OperationId, binding.OperationId, StringComparison.Ordinal)
            || exact.Payload.EffectGeneration != binding.EffectGeneration
            || !string.Equals(exact.Payload.IntentHash, binding.IntentHash, StringComparison.Ordinal))
        {
            throw new ArgumentException("A probe effect head must be the exact retained reconciliation-required effect bound by the request.", parameterName);
        }

        return exact;
    }

    internal static GovernedLoopEffectAttempt CopyRequiredProbeEffect(
        GovernedLoopEffectAttempt? value,
        GovernedLoopEffectReconciliationCaseReference caseReference,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        var exact = CopyOptionalAttempt(value, parameterName)!;
        if (exact.Payload.Phase != GovernedLoopEffectPhase.ReconciliationRequired)
        {
            throw new ArgumentException("A probe reservation must retain a reconciliation-required effect head.", parameterName);
        }

        return exact;
    }

    internal static GovernedLoopEffectReconciliationEvidenceSource CopyProbeSource(
        GovernedLoopEffectReconciliationEvidenceSource? value,
        GovernedLoopEffectReconciliationCaseReference caseReference,
        GovernedLoopEffectReconciliationBinding binding,
        GovernedLoopEffectReconciliationContractMetadata contract,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);

        if (!GovernedLoopEffectReconciliationContractValidator.Validate(value).IsValid
            || !string.Equals(value.CaseId, caseReference.CaseId, StringComparison.Ordinal)
            || !string.Equals(value.BindingHash, binding.ContentHash, StringComparison.Ordinal)
            || !string.Equals(value.ReconciliationContractId, contract.ContractId, StringComparison.Ordinal)
            || value.ReconciliationContractVersion != contract.ContractVersion
            || !string.Equals(value.ReconciliationContractHash, contract.ContentHash, StringComparison.Ordinal))
        {
            throw new ArgumentException("A probe source must be the exact retained source registration and probe contract bound by the request.", parameterName);
        }

        return GovernedLoopEffectReconciliationContractCopy.Copy(value)!;
    }

    internal static GovernedLoopEffectReconciliationEvidenceSource CopyRequiredProbeSource(
        GovernedLoopEffectReconciliationEvidenceSource? value,
        GovernedLoopEffectReconciliationCaseReference caseReference,
        GovernedLoopEffectReconciliationContractMetadata contract,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        var exact = GovernedLoopEffectReconciliationContractValidator.Validate(value).IsValid
            ? GovernedLoopEffectReconciliationContractCopy.Copy(value)
            : throw new ArgumentException("A probe reservation must retain a canonical source registration.", parameterName);
        if (!string.Equals(exact.CaseId, caseReference.CaseId, StringComparison.Ordinal)
            || !string.Equals(exact.BindingHash, caseReference.BindingHash, StringComparison.Ordinal)
            || !string.Equals(exact.ReconciliationContractId, contract.ContractId, StringComparison.Ordinal)
            || exact.ReconciliationContractVersion != contract.ContractVersion
            || !string.Equals(exact.ReconciliationContractHash, contract.ContentHash, StringComparison.Ordinal))
        {
            throw new ArgumentException("A probe reservation source is not bound to its exact case and contract.", parameterName);
        }

        return exact;
    }

    internal static GovernedLoopEffectReconciliationProbeInvocationRequest CopyRequiredProbeInvocation(
        GovernedLoopEffectReconciliationProbeInvocationRequest? value,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.EffectHead is null || value.Source is null)
        {
            throw new ArgumentException("A durable probe reservation requires the exact retained effect head and source registration.", parameterName);
        }

        return new GovernedLoopEffectReconciliationProbeInvocationRequest(
            value.Case,
            value.Binding,
            value.Contract,
            value.Input,
            value.EffectHead,
            value.Source);
    }

    internal static GovernedLoopEffectReconciliationProbeInvocationResult CopyRequiredProbeResult(
        GovernedLoopEffectReconciliationProbeInvocationResult? value,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        return new GovernedLoopEffectReconciliationProbeInvocationResult(value.Status, value.Observation);
    }

    internal static GovernedLoopEffectReconciliationProbeReservation CopyRequiredReservation(
        GovernedLoopEffectReconciliationProbeReservation? value,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        return new GovernedLoopEffectReconciliationProbeReservation(value.OperationId, value.RequestHash, value.Case, value.EffectHead, value.Source, value.Contract, value.ReservedAtUtc);
    }

    internal static GovernedLoopEffectReconciliationProbeReservation? CopyReservationPayload(
        GovernedLoopEffectReconciliationProbeReservationStatus status,
        GovernedLoopEffectReconciliationProbeReservation? value,
        string parameterName)
    {
        var required = status is GovernedLoopEffectReconciliationProbeReservationStatus.Reserved or GovernedLoopEffectReconciliationProbeReservationStatus.Replayed;
        return required ? CopyRequiredReservation(value, parameterName) : value is null ? null : throw new ArgumentException("Only a reserved or replayed probe result may carry a reservation.", parameterName);
    }

    internal static GovernedLoopEffectReconciliationCase? CopyProbeCommitCase(
        GovernedLoopEffectReconciliationProbeReservationStatus status,
        GovernedLoopEffectReconciliationCase? value,
        GovernedLoopEffectAttempt? effect,
        string parameterName)
    {
        var hasPayload = status is GovernedLoopEffectReconciliationProbeReservationStatus.Reserved or GovernedLoopEffectReconciliationProbeReservationStatus.Replayed;
        if (!hasPayload && (value is not null || effect is not null))
        {
            throw new ArgumentException("A non-successful probe commit must omit case and effect payloads.", parameterName);
        }

        if (hasPayload && (value is null || effect is null))
        {
            throw new ArgumentException("A successful probe commit requires its exact case and unchanged effect head.", parameterName);
        }

        if (value is null)
        {
            return null;
        }

        var exactCase = CopyRequiredCase(value, parameterName);
        var exactEffect = CopyOptionalAttempt(effect, nameof(effect));
        if (exactEffect is null || !IsCurrentAttempt(exactCase.Binding, exactEffect))
        {
            throw new ArgumentException("A successful probe commit must return the unchanged reconciliation-required effect head.", parameterName);
        }

        return exactCase;
    }

    internal static GovernedLoopEffectReconciliationCase? CopyProbeReplayCase(
        GovernedLoopEffectReconciliationProbeReservationStatus status,
        GovernedLoopEffectReconciliationCase? value,
        GovernedLoopEffectAttempt? effect,
        string parameterName)
    {
        if (status != GovernedLoopEffectReconciliationProbeReservationStatus.Replayed && (value is not null || effect is not null))
        {
            throw new ArgumentException("Only a replayed probe reservation may carry a completed case payload.", parameterName);
        }

        if ((value is null) != (effect is null))
        {
            throw new ArgumentException("A replayed probe reservation must carry both its case and unchanged effect head or neither.", parameterName);
        }

        if (value is null)
        {
            return null;
        }

        var exactCase = CopyRequiredCase(value, parameterName);
        var exactEffect = CopyOptionalAttempt(effect, nameof(effect));
        if (exactEffect is null || !IsCurrentAttempt(exactCase.Binding, exactEffect))
        {
            throw new ArgumentException("A replayed probe reservation must return the unchanged reconciliation-required effect head.", parameterName);
        }

        return exactCase;
    }

    internal static GovernedLoopEffectAttempt? CopyProbeReplayEffect(
        GovernedLoopEffectReconciliationProbeReservationStatus status,
        GovernedLoopEffectReconciliationCase? value,
        GovernedLoopEffectAttempt? effect,
        string parameterName)
    {
        _ = CopyProbeReplayCase(status, value, effect, nameof(value));
        return effect is null ? null : CopyOptionalAttempt(effect, parameterName);
    }

    internal static GovernedLoopEffectAttempt? CopyProbeCommitEffect(
        GovernedLoopEffectReconciliationProbeReservationStatus status,
        GovernedLoopEffectReconciliationCase? value,
        GovernedLoopEffectAttempt? effect,
        string parameterName)
    {
        _ = CopyProbeCommitCase(status, value, effect, nameof(value));
        return effect is null ? null : CopyOptionalAttempt(effect, parameterName);
    }

    internal static DateTimeOffset RequireUtc(DateTimeOffset value, string parameterName)
        => value != default && value.Offset == TimeSpan.Zero ? value : throw new ArgumentException("A durable probe timestamp must be UTC.", parameterName);

    internal static GovernedLoopEffectReconciliationBinding CopyMutationBinding(
        GovernedLoopEffectReconciliationBinding? binding,
        GovernedLoopEffectReconciliationCase? replacement,
        GovernedLoopEffectAttempt? successor,
        string parameterName)
    {
        var exactBinding = CopyRequiredBinding(binding, parameterName);
        var exactReplacement = CopyRequiredCase(replacement, nameof(replacement));
        if (!Equals(exactBinding, exactReplacement.Binding))
        {
            throw new ArgumentException("The replacement case must retain the exact requested reconciliation binding.", parameterName);
        }

        if (successor is not null && !IsBoundReconciledSuccessor(exactBinding, exactReplacement, successor))
        {
            throw new ArgumentException("The optional effect successor must be valid and exactly bound to the accepted replacement resolution.", nameof(successor));
        }

        return exactBinding;
    }

    internal static GovernedLoopEffectAttempt? CopyOptionalAttempt(GovernedLoopEffectAttempt? value, string parameterName)
    {
        if (value is not null && GovernedLoopEffectAttemptContract.Validate(value) is not null)
        {
            throw new ArgumentException("An effect-attempt payload must be complete, canonical, and hash-valid.", parameterName);
        }

        return GovernedLoopEffectReconciliationApplicationCopy.Copy(value);
    }

    internal static IReadOnlyList<T> CaptureResultPage<TStatus, T>(
        TStatus status,
        TStatus readyStatus,
        IReadOnlyList<T> values,
        string? nextCursor,
        Func<T, T> copy,
        string parameterName)
        where TStatus : struct, Enum
    {
        RequireDefinedStatus(status, nameof(status));
        var captured = GovernedLoopEffectReconciliationPageLimits.CapturePage(values, copy, parameterName);
        if (!EqualityComparer<TStatus>.Default.Equals(status, readyStatus) && (captured.Count != 0 || nextCursor is not null))
        {
            throw new ArgumentException("Only a ready reconciliation list result may carry entries or a continuation cursor.", parameterName);
        }

        return captured;
    }

    internal static string? CaptureResultCursor<TStatus>(TStatus status, TStatus readyStatus, string? cursor, string parameterName)
        where TStatus : struct, Enum
    {
        RequireDefinedStatus(status, nameof(status));
        if (!EqualityComparer<TStatus>.Default.Equals(status, readyStatus) && cursor is not null)
        {
            throw new ArgumentException("Only a ready reconciliation list result may carry a continuation cursor.", parameterName);
        }

        return GovernedLoopEffectReconciliationPageLimits.CaptureCursor(cursor, parameterName);
    }

    internal static GovernedLoopEffectReconciliationCase? CopyCaseReadPayload(
        GovernedLoopEffectReconciliationCaseReadStatus status,
        GovernedLoopEffectReconciliationCase? value,
        string parameterName)
        => CopyRequiredPayload(status == GovernedLoopEffectReconciliationCaseReadStatus.Found, value, CopyRequiredCase, parameterName);

    internal static GovernedLoopEffectReconciliationCase? CopyMutationResultCase(
        GovernedLoopEffectReconciliationCaseMutationStatus status,
        GovernedLoopEffectReconciliationCase? value,
        GovernedLoopEffectAttempt? effectHead,
        string parameterName)
    {
        var hasCurrentState = status is GovernedLoopEffectReconciliationCaseMutationStatus.Applied
            or GovernedLoopEffectReconciliationCaseMutationStatus.Replayed
            or GovernedLoopEffectReconciliationCaseMutationStatus.Conflict;
        if (hasCurrentState != (value is not null && effectHead is not null)
            || !hasCurrentState && (value is not null || effectHead is not null))
        {
            throw new ArgumentException("Applied, replayed, and conflict mutations require the exact current case and effect head; every other status must omit both.", parameterName);
        }

        var exactCase = value is null ? null : CopyRequiredCase(value, parameterName);
        var exactEffectHead = effectHead is null ? null : CopyOptionalAttempt(effectHead, nameof(effectHead));
        if (exactCase is not null
            && exactEffectHead is not null
            && !IsCurrentAttempt(exactCase.Binding, exactEffectHead)
            && !IsBoundReconciledSuccessor(exactCase.Binding, exactCase, exactEffectHead))
        {
            throw new ArgumentException("The returned current effect head must exactly match the returned reconciliation case binding and resolution posture.", parameterName);
        }

        return exactCase;
    }

    internal static GovernedLoopEffectAttempt? CopyMutationResultEffect(
        GovernedLoopEffectReconciliationCaseMutationStatus status,
        GovernedLoopEffectReconciliationCase? reconciliationCase,
        GovernedLoopEffectAttempt? effectHead,
        string parameterName)
    {
        _ = CopyMutationResultCase(status, reconciliationCase, effectHead, nameof(reconciliationCase));
        return CopyOptionalAttempt(effectHead, parameterName);
    }

    internal static string CopyAuthorizationPurpose(
        GovernedLoopEffectReconciliationAuthorizationStatus status,
        string? purpose,
        GovernedLoopEffectReconciliationCaseReference? caseReference,
        GovernedLoopEffectReconciliationBinding? binding,
        string? authorityEvidenceHash,
        string parameterName)
    {
        var ready = status == GovernedLoopEffectReconciliationAuthorizationStatus.Ready;
        if (purpose is null || caseReference is null || binding is null || ready != (authorityEvidenceHash is not null))
        {
            throw new ArgumentException("Every authorization result requires its exact purpose, case, and binding; only ready authorization may carry authority evidence.", parameterName);
        }

        _ = CopyBoundBinding(caseReference, binding, nameof(binding));
        if (authorityEvidenceHash is not null)
        {
            _ = RequireSha256(authorityEvidenceHash, nameof(authorityEvidenceHash));
        }

        return RequireIdentifier(purpose, parameterName);
    }

    internal static GovernedLoopEffectReconciliationCaseReference CopyAuthorizationCase(GovernedLoopEffectReconciliationCaseReference? value, string parameterName)
        => CopyRequiredReference(value, parameterName);

    internal static GovernedLoopEffectReconciliationBinding CopyAuthorizationBinding(
        GovernedLoopEffectReconciliationCaseReference? caseReference,
        GovernedLoopEffectReconciliationBinding? binding,
        string parameterName)
        => CopyBoundBinding(caseReference, binding, parameterName);

    internal static string? CopyAuthorizationEvidenceHash(GovernedLoopEffectReconciliationAuthorizationStatus status, string? value, string parameterName)
        => CopyRequiredPayload(status == GovernedLoopEffectReconciliationAuthorizationStatus.Ready, value, RequireSha256, parameterName);

    internal static GovernedLoopEffectReconciliationContractMetadata? CopyRegistryReadContract(
        GovernedLoopEffectReconciliationProbeRegistryReadStatus status,
        GovernedLoopEffectReconciliationContractMetadata? contract,
        IGovernedLoopEffectReconciliationProbe? probe,
        string parameterName)
    {
        var found = status == GovernedLoopEffectReconciliationProbeRegistryReadStatus.Found;
        var conflict = status == GovernedLoopEffectReconciliationProbeRegistryReadStatus.Conflict;
        if (found != (contract is not null && probe is not null)
            || conflict != (contract is not null && probe is null)
            || !found && !conflict && (contract is not null || probe is not null))
        {
            throw new ArgumentException("A found registry result requires exact contract metadata and its probe; conflict returns only canonical current metadata; every other status omits both.", parameterName);
        }

        return contract is null ? null : CopyRequiredMetadata(contract, parameterName);
    }

    internal static IGovernedLoopEffectReconciliationProbe? RequireRegistryReadProbe(
        GovernedLoopEffectReconciliationProbeRegistryReadStatus status,
        GovernedLoopEffectReconciliationContractMetadata? contract,
        IGovernedLoopEffectReconciliationProbe? probe,
        string parameterName)
    {
        _ = CopyRegistryReadContract(status, contract, probe, nameof(contract));
        return probe;
    }

    internal static GovernedLoopEffectReconciliationObservation? CopyProbePayload(
        GovernedLoopEffectReconciliationProbeInvocationStatus status,
        GovernedLoopEffectReconciliationObservation? value,
        string parameterName)
        => CopyRequiredPayload(
            status == GovernedLoopEffectReconciliationProbeInvocationStatus.Ready,
            value,
            static (observation, name) => GovernedLoopEffectReconciliationContractValidator.Validate(observation).IsValid
                ? GovernedLoopEffectReconciliationContractCopy.Copy(observation)
                : throw new ArgumentException("A probe observation must be complete, canonical, and hash-valid.", name),
            parameterName);

    internal static GovernedLoopEffectReconciliationBinding? CopyInputReadBinding(
        GovernedLoopEffectReconciliationInputReadStatus status,
        GovernedLoopEffectReconciliationCaseReference? caseReference,
        GovernedLoopEffectReconciliationBinding? binding,
        GovernedLoopEffectAttempt? effectHead,
        GovernedLoopFrontierPosture? frontier,
        GovernedActuatorInputEvidence? input,
        string parameterName)
    {
        var found = status == GovernedLoopEffectReconciliationInputReadStatus.Found;
        var hasAll = caseReference is not null && binding is not null && effectHead is not null && frontier is not null && input is not null;
        var hasAny = caseReference is not null || binding is not null || effectHead is not null || frontier is not null || input is not null;
        if (found != hasAll || !found && hasAny)
        {
            throw new ArgumentException("Only a found input read may carry the complete case, binding, effect head, ReviewBlocked frontier, and canonical input.", parameterName);
        }

        if (!found)
        {
            return null;
        }

        var exactBinding = CopyBoundBinding(caseReference, binding, parameterName);
        if (!GovernedLoopFrontierContractValidator.Validate(frontier).IsValid
            || !IsCurrentAttempt(exactBinding, effectHead!)
            || !IsMatchingReviewBlockedFrontier(exactBinding, frontier!))
        {
            throw new ArgumentException("A found input read must retain the exact reconciliation-required effect head and matching ReviewBlocked frontier activation.", parameterName);
        }

        _ = CopyRequiredInput(input, nameof(input));
        return exactBinding;
    }

    internal static GovernedLoopEffectReconciliationCaseReference? CopyInputReadCase(GovernedLoopEffectReconciliationInputReadStatus status, GovernedLoopEffectReconciliationCaseReference? value, string parameterName)
        => CopyRequiredPayload(status == GovernedLoopEffectReconciliationInputReadStatus.Found, value, CopyRequiredReference, parameterName);

    internal static GovernedLoopEffectAttempt? CopyInputReadEffect(GovernedLoopEffectReconciliationInputReadStatus status, GovernedLoopEffectAttempt? value, string parameterName)
        => CopyRequiredPayload(status == GovernedLoopEffectReconciliationInputReadStatus.Found, value, CopyRequiredAttempt, parameterName);

    internal static GovernedLoopFrontierPosture? CopyInputReadFrontier(GovernedLoopEffectReconciliationInputReadStatus status, GovernedLoopFrontierPosture? value, string parameterName)
        => CopyRequiredPayload(status == GovernedLoopEffectReconciliationInputReadStatus.Found, value, CopyRequiredFrontier, parameterName);

    internal static GovernedActuatorInputEvidence? CopyInputReadInput(GovernedLoopEffectReconciliationInputReadStatus status, GovernedActuatorInputEvidence? value, string parameterName)
        => CopyRequiredPayload(status == GovernedLoopEffectReconciliationInputReadStatus.Found, value, CopyRequiredInput, parameterName);

    internal static GovernedLoopEffectReconciliationResolution? CopyResolutionPayload(
        GovernedLoopEffectReconciliationResolutionReadStatus status,
        GovernedLoopEffectReconciliationResolution? value,
        string parameterName)
        => CopyRequiredPayload(
            status == GovernedLoopEffectReconciliationResolutionReadStatus.Found,
            value,
            static (resolution, name) => GovernedLoopEffectReconciliationContractValidator.Validate(resolution).IsValid
                ? GovernedLoopEffectReconciliationContractCopy.Copy(resolution)!
                : throw new ArgumentException("A reconciliation resolution must be complete, canonical, and hash-valid.", name),
            parameterName);

    private static T? CopyRequiredPayload<T>(bool required, T? value, Func<T, string, T> copy, string parameterName)
        where T : class
    {
        if (required != (value is not null))
        {
            throw new ArgumentException(required ? "The successful reconciliation status requires its exact payload." : "A non-success reconciliation status must not carry a payload.", parameterName);
        }

        return value is null ? null : copy(value, parameterName);
    }

    private static GovernedLoopEffectAttempt CopyRequiredAttempt(GovernedLoopEffectAttempt value, string parameterName)
        => CopyOptionalAttempt(value, parameterName)!;

    private static GovernedLoopFrontierPosture CopyRequiredFrontier(GovernedLoopFrontierPosture value, string parameterName)
    {
        if (!GovernedLoopFrontierContractValidator.Validate(value).IsValid)
        {
            throw new ArgumentException("A reconciliation frontier must be complete, canonical, and hash-valid.", parameterName);
        }

        return GovernedLoopEffectReconciliationApplicationCopy.Copy(value)!;
    }

    private static bool IsBoundReconciledSuccessor(
        GovernedLoopEffectReconciliationBinding binding,
        GovernedLoopEffectReconciliationCase replacement,
        GovernedLoopEffectAttempt successor)
    {
        var resolution = replacement.Resolution;
        return resolution is not null
            && GovernedLoopEffectAttemptContract.Validate(successor) is null
            && IsMatchingAttemptIdentity(binding, successor)
            && Equals(replacement.ContractMetadata.Capability, successor.Capability)
            && Equals(replacement.ContractMetadata.Implementation, successor.Implementation)
            && string.Equals(replacement.ContractMetadata.ActuatorOperationId, successor.ActuatorOperationId, StringComparison.Ordinal)
            && string.Equals(replacement.ContractMetadata.OperationDescriptorHash, successor.OperationDescriptorHash, StringComparison.Ordinal)
            && string.Equals(binding.CurrentAttemptHash, successor.PreviousContentHash, StringComparison.Ordinal)
            && successor.Payload.Phase == GovernedLoopEffectPhase.Reconciled
            && successor.Payload.Outcome == resolution.Outcome
            && successor.Payload.EvidenceStatus == GovernedLoopEffectEvidenceStatus.Complete
            && string.Equals(successor.Payload.OutcomeEvidenceId, resolution.OutcomeEvidenceId, StringComparison.Ordinal)
            && string.Equals(successor.Payload.ReconciliationEvidenceId, resolution.ResolutionId, StringComparison.Ordinal)
            && successor.Payload.UpdatedAtUtc == resolution.ResolvedAtUtc;
    }

    private static bool IsCurrentAttempt(GovernedLoopEffectReconciliationBinding binding, GovernedLoopEffectAttempt attempt)
        => GovernedLoopEffectAttemptContract.Validate(attempt) is null
            && IsMatchingAttemptIdentity(binding, attempt)
            && string.Equals(binding.CurrentAttemptHash, attempt.ContentHash, StringComparison.Ordinal)
            && attempt.Payload.Phase == GovernedLoopEffectPhase.ReconciliationRequired;

    private static bool IsMatchingAttemptIdentity(GovernedLoopEffectReconciliationBinding binding, GovernedLoopEffectAttempt attempt)
        => Equals(binding.Execution, attempt.Binding)
            && string.Equals(binding.NodeId, attempt.NodeId, StringComparison.Ordinal)
            && binding.NodeAttempt == attempt.NodeAttempt
            && string.Equals(binding.EffectId, attempt.Payload.EffectId, StringComparison.Ordinal)
            && string.Equals(binding.OperationId, attempt.Payload.OperationId, StringComparison.Ordinal)
            && binding.EffectGeneration == attempt.Payload.EffectGeneration
            && string.Equals(binding.IntentHash, attempt.Payload.IntentHash, StringComparison.Ordinal);

    private static bool IsMatchingReviewBlockedFrontier(GovernedLoopEffectReconciliationBinding binding, GovernedLoopFrontierPosture frontier)
        => Equals(binding.Execution, frontier.Binding)
            && string.Equals(binding.WorkspaceId, frontier.WorkspaceId, StringComparison.Ordinal)
            && frontier.Payload.Status == GovernedLoopFrontierStatus.ReviewBlocked
            && frontier.Payload.Nodes.Any(node => node.ActivationOrdinal == binding.ActivationOrdinal
                && node.VisitOrdinal == binding.VisitOrdinal
                && string.Equals(node.NodeId, binding.NodeId, StringComparison.Ordinal)
                && node.Status == GovernedLoopNodeExecutionStatus.ReviewBlocked
                && node.Attempt == binding.NodeAttempt
                && string.Equals(node.AttemptOperationId, binding.OperationId, StringComparison.Ordinal));
}
