using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Loops.Execution.Models;

namespace EmbodySense.Core.Common.Loops.Execution.Effects;

/// <summary>Creates, validates, hashes, and advances immutable value-free effect-attempt evidence.</summary>
public static class GovernedLoopEffectAttemptContract
{
    private const string AttemptDomain = "embodysense.governed-loop-effect-attempt.v1";
    private const string IntentDomain = "embodysense.governed-loop-effect-intent.v1";

    /// <summary>Creates the initial durable intent before current authority evaluation or adapter dispatch.</summary>
    public static GovernedLoopEffectAttempt Prepare(
        GovernedLoopExecutionBinding binding,
        string nodeId,
        int nodeAttempt,
        CapabilityDescriptorIdentity capability,
        CapabilityImplementationIdentity implementation,
        string actuatorOperationId,
        string operationDescriptorHash,
        string effectId,
        string idempotencyOperationId,
        long effectGeneration,
        string inputFingerprint,
        string targetFingerprint,
        string? preconditionEvidenceHash,
        string admissionAuthorityEvidenceHash,
        string? beforeEvidenceId,
        DateTimeOffset preparedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(binding);
        var intentHash = ComputeIntent(
            binding,
            nodeId,
            nodeAttempt,
            capability,
            implementation,
            actuatorOperationId,
            operationDescriptorHash,
            effectId,
            idempotencyOperationId,
            effectGeneration,
            inputFingerprint,
            targetFingerprint,
            preconditionEvidenceHash,
            admissionAuthorityEvidenceHash,
            beforeEvidenceId);
        var payload = GovernedLoopEffectPayload.Create(
            GovernedLoopEffectAttemptContractLimits.CurrentSchemaVersion,
            effectId,
            idempotencyOperationId,
            effectGeneration,
            GovernedLoopEffectOrigin.Actuator,
            nodeId,
            intentHash,
            GovernedLoopEffectPhase.IntentPrepared,
            GovernedLoopEffectOutcome.None,
            GovernedLoopEffectEvidenceStatus.Pending,
            null,
            null,
            preparedAtUtc);
        var attempt = new GovernedLoopEffectAttempt(
            GovernedLoopEffectAttemptContractLimits.CurrentSchemaVersion,
            binding,
            nodeId,
            nodeAttempt,
            capability,
            implementation,
            actuatorOperationId,
            operationDescriptorHash,
            inputFingerprint,
            targetFingerprint,
            preconditionEvidenceHash,
            admissionAuthorityEvidenceHash,
            null,
            beforeEvidenceId,
            null,
            payload,
            null,
            string.Empty);
        var error = ValidateForHash(attempt, requireHash: false);
        if (error is not null)
        {
            throw new ArgumentException(error, nameof(binding));
        }

        return attempt with { ContentHash = Compute(attempt) };
    }

    /// <summary>Attaches the fresh exact authority decision while preserving the initial intent and phase.</summary>
    public static GovernedLoopEffectAttempt AttachDispatchAuthority(
        GovernedLoopEffectAttempt current,
        string dispatchAuthorityEvidenceHash,
        DateTimeOffset observedAtUtc)
    {
        RequireCurrent(current);
        if (current.Payload.Phase != GovernedLoopEffectPhase.IntentPrepared
            || current.DispatchAuthorityEvidenceHash is not null
            || !IsCanonicalSha256(dispatchAuthorityEvidenceHash)
            || !IsUtcAtOrAfter(observedAtUtc, current.Payload.UpdatedAtUtc))
        {
            throw new InvalidOperationException("Fresh dispatch authority may be attached exactly once to a prepared intent.");
        }

        var payload = GovernedLoopEffectPayload.Create(
            current.Payload.SchemaVersion,
            current.Payload.EffectId,
            current.Payload.OperationId,
            current.Payload.EffectGeneration,
            current.Payload.Origin,
            current.Payload.OriginNodeId,
            current.Payload.IntentHash,
            current.Payload.Phase,
            current.Payload.Outcome,
            current.Payload.EvidenceStatus,
            current.Payload.OutcomeEvidenceId,
            current.Payload.ReconciliationEvidenceId,
            observedAtUtc);
        return ApplySuccessor(current, payload, dispatchAuthorityEvidenceHash, current.AfterEvidenceId);
    }

    /// <summary>Advances one attempt through an explicit legal executor-neutral effect phase.</summary>
    public static GovernedLoopEffectAttempt Advance(
        GovernedLoopEffectAttempt current,
        GovernedLoopEffectPhase phase,
        GovernedLoopEffectOutcome outcome,
        GovernedLoopEffectEvidenceStatus evidenceStatus,
        string? outcomeEvidenceId,
        string? afterEvidenceId,
        DateTimeOffset updatedAtUtc)
    {
        RequireCurrent(current);
        if (current.DispatchAuthorityEvidenceHash is null && phase != GovernedLoopEffectPhase.DispatchNotStarted)
        {
            throw new InvalidOperationException("A fresh durable authority decision is required before effect state may advance.");
        }
        if (phase == current.Payload.Phase
            || !GovernedLoopExecutionStateMatrix.IsEffectTransitionAllowed(current.Payload.Phase, phase)
            || !IsUtcAtOrAfter(updatedAtUtc, current.Payload.UpdatedAtUtc))
        {
            throw new InvalidOperationException("The requested effect-attempt phase is not a legal successor.");
        }

        var payload = GovernedLoopEffectPayload.Create(
            current.Payload.SchemaVersion,
            current.Payload.EffectId,
            current.Payload.OperationId,
            current.Payload.EffectGeneration,
            current.Payload.Origin,
            current.Payload.OriginNodeId,
            current.Payload.IntentHash,
            phase,
            outcome,
            evidenceStatus,
            outcomeEvidenceId,
            null,
            updatedAtUtc);
        var currentPosture = GovernedLoopEffectPosture.Create(current.Binding, current.Payload);
        var nextPosture = GovernedLoopEffectPosture.Create(current.Binding, payload);
        if (!GovernedLoopExecutionValidator.ValidateTransition(currentPosture, nextPosture).IsValid)
        {
            throw new InvalidOperationException("The requested effect evidence is not a legal executor-neutral successor.");
        }

        return ApplySuccessor(current, payload, current.DispatchAuthorityEvidenceHash, afterEvidenceId);
    }

    /// <summary>Returns a bounded structured reason code when an attempt is invalid; otherwise <see langword="null"/>.</summary>
    public static string? Validate(GovernedLoopEffectAttempt? attempt)
        => ValidateForHash(attempt, requireHash: true);

    /// <summary>Computes one attempt version's canonical domain-separated content hash.</summary>
    public static string Compute(GovernedLoopEffectAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        var error = ValidateForHash(attempt, requireHash: false);
        if (error is not null)
        {
            throw new ArgumentException(error, nameof(attempt));
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendAttemptIdentity(hash, AttemptDomain, attempt);
        Append(hash, attempt.DispatchAuthorityEvidenceHash);
        Append(hash, attempt.AfterEvidenceId);
        Append(hash, attempt.Payload.Phase);
        Append(hash, attempt.Payload.Outcome);
        Append(hash, attempt.Payload.EvidenceStatus);
        Append(hash, attempt.Payload.OutcomeEvidenceId);
        Append(hash, attempt.Payload.ReconciliationEvidenceId);
        Append(hash, attempt.Payload.UpdatedAtUtc);
        Append(hash, attempt.PreviousContentHash);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>Identifies whether two attempts name the exact same immutable authorized intent.</summary>
    public static bool HasSameIntent(GovernedLoopEffectAttempt left, GovernedLoopEffectAttempt right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return string.Equals(left.Payload.IntentHash, right.Payload.IntentHash, StringComparison.Ordinal)
            && string.Equals(left.Payload.OperationId, right.Payload.OperationId, StringComparison.Ordinal)
            && left.Payload.EffectGeneration == right.Payload.EffectGeneration;
    }

    /// <summary>Returns whether one validated attempt version is the direct, immutable successor of another.</summary>
    public static bool IsDirectSuccessor(GovernedLoopEffectAttempt current, GovernedLoopEffectAttempt next)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(next);
        if (Validate(current) is not null
            || Validate(next) is not null
            || !HasSameIntent(current, next)
            || !string.Equals(current.ContentHash, next.PreviousContentHash, StringComparison.Ordinal)
            || !SameImmutableIntent(current, next)
            || next.Payload.UpdatedAtUtc < current.Payload.UpdatedAtUtc)
        {
            return false;
        }

        var authorityAttachment = current.Payload.Phase == GovernedLoopEffectPhase.IntentPrepared
            && next.Payload.Phase == GovernedLoopEffectPhase.IntentPrepared
            && current.DispatchAuthorityEvidenceHash is null
            && next.DispatchAuthorityEvidenceHash is not null
            && SamePayloadExceptTime(current.Payload, next.Payload)
            && string.Equals(current.AfterEvidenceId, next.AfterEvidenceId, StringComparison.Ordinal);
        if (authorityAttachment)
        {
            return true;
        }

        if (!string.Equals(current.DispatchAuthorityEvidenceHash, next.DispatchAuthorityEvidenceHash, StringComparison.Ordinal)
            || current.AfterEvidenceId is not null && !string.Equals(current.AfterEvidenceId, next.AfterEvidenceId, StringComparison.Ordinal))
        {
            return false;
        }
        if (!IsProtocolTransition(current.Payload.Phase, next.Payload.Phase))
        {
            return false;
        }
        var currentPosture = GovernedLoopEffectPosture.Create(current.Binding, current.Payload);
        var nextPosture = GovernedLoopEffectPosture.Create(next.Binding, next.Payload);
        return GovernedLoopExecutionValidator.ValidateTransition(currentPosture, nextPosture).IsValid;
    }

    private static GovernedLoopEffectAttempt ApplySuccessor(
        GovernedLoopEffectAttempt current,
        GovernedLoopEffectPayload payload,
        string? dispatchAuthorityEvidenceHash,
        string? afterEvidenceId)
    {
        if (!IsOptionalEvidenceReference(afterEvidenceId))
        {
            throw new ArgumentException("After-evidence references must be bounded canonical identifiers.", nameof(afterEvidenceId));
        }
        if (afterEvidenceId is not null && payload.Phase is not (GovernedLoopEffectPhase.OutcomeObserved or GovernedLoopEffectPhase.Committed or GovernedLoopEffectPhase.ReconciliationRequired or GovernedLoopEffectPhase.Reconciled))
        {
            throw new InvalidOperationException("After-state evidence may appear only after an outcome was observed or reconciliation became necessary.");
        }

        var successor = current with
        {
            DispatchAuthorityEvidenceHash = dispatchAuthorityEvidenceHash,
            AfterEvidenceId = afterEvidenceId,
            Payload = payload with { },
            PreviousContentHash = current.ContentHash,
            ContentHash = string.Empty,
        };
        var error = ValidateForHash(successor, requireHash: false);
        if (error is not null)
        {
            throw new InvalidOperationException(error);
        }
        var hashed = successor with { ContentHash = Compute(successor) };
        if (!IsDirectSuccessor(current, hashed))
        {
            throw new InvalidOperationException("The requested effect attempt is not a direct immutable successor.");
        }
        return hashed;
    }

    private static string ComputeIntent(
        GovernedLoopExecutionBinding binding,
        string nodeId,
        int nodeAttempt,
        CapabilityDescriptorIdentity capability,
        CapabilityImplementationIdentity implementation,
        string actuatorOperationId,
        string operationDescriptorHash,
        string effectId,
        string idempotencyOperationId,
        long effectGeneration,
        string inputFingerprint,
        string targetFingerprint,
        string? preconditionEvidenceHash,
        string admissionAuthorityEvidenceHash,
        string? beforeEvidenceId)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, IntentDomain);
        Append(hash, GovernedLoopEffectAttemptContractLimits.CurrentSchemaVersion);
        AppendBinding(hash, binding);
        Append(hash, nodeId);
        Append(hash, nodeAttempt);
        Append(hash, capability?.Id?.Value);
        Append(hash, capability?.Version?.Value);
        Append(hash, capability?.Hash?.Value);
        Append(hash, implementation?.ProviderId?.Value);
        Append(hash, implementation?.ImplementationId);
        Append(hash, actuatorOperationId);
        Append(hash, operationDescriptorHash);
        Append(hash, effectId);
        Append(hash, idempotencyOperationId);
        Append(hash, effectGeneration);
        Append(hash, inputFingerprint);
        Append(hash, targetFingerprint);
        Append(hash, preconditionEvidenceHash);
        Append(hash, admissionAuthorityEvidenceHash);
        Append(hash, beforeEvidenceId);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string? ValidateForHash(GovernedLoopEffectAttempt? attempt, bool requireHash)
    {
        if (attempt is null)
        {
            return "effect-attempt-required";
        }
        if (attempt.SchemaVersion != GovernedLoopEffectAttemptContractLimits.CurrentSchemaVersion
            || attempt.Binding is null
            || !GovernedLoopExecutionValidator.Validate(attempt.Binding).IsValid)
        {
            return "effect-attempt-binding-invalid";
        }
        if (!IsIdentifier(attempt.NodeId)
            || attempt.NodeAttempt is < 1 or > GovernedLoopExecutionLimits.MaxNodeAttempt
            || attempt.Payload is null
            || !string.Equals(attempt.NodeId, attempt.Payload.OriginNodeId, StringComparison.Ordinal)
            || attempt.Payload.Origin != GovernedLoopEffectOrigin.Actuator
            || !GovernedLoopExecutionValidator.Validate(attempt.Payload).IsValid)
        {
            return "effect-attempt-node-or-payload-invalid";
        }
        if (attempt.Capability?.Id is null
            || attempt.Capability.Version is null
            || attempt.Capability.Hash is null
            || !CapabilityId.TryParse(attempt.Capability.Id.Value, out _, out _)
            || !CapabilityVersion.TryParse(attempt.Capability.Version.Value, out _, out _)
            || !CapabilityDescriptorHash.TryParse(attempt.Capability.Hash.Value, out _, out _)
            || attempt.Implementation?.ProviderId is null
            || !CapabilityProviderId.TryParse(attempt.Implementation.ProviderId.Value, out _, out _)
            || !CapabilityIdentifierRules.IsPath(attempt.Implementation.ImplementationId, CapabilityContractLimits.MaxImplementationIdCharacters))
        {
            return "effect-attempt-capability-pin-invalid";
        }
        if (!CapabilityIdentifierRules.IsPath(attempt.ActuatorOperationId, GovernedLoopEffectAttemptContractLimits.MaxOperationIdCharacters)
            || !IsCanonicalSha256(attempt.OperationDescriptorHash)
            || !IsCanonicalSha256(attempt.InputFingerprint)
            || !IsCanonicalSha256(attempt.TargetFingerprint)
            || attempt.PreconditionEvidenceHash is not null && !IsCanonicalSha256(attempt.PreconditionEvidenceHash)
            || !IsCanonicalSha256(attempt.AdmissionAuthorityEvidenceHash)
            || attempt.DispatchAuthorityEvidenceHash is not null && !IsCanonicalSha256(attempt.DispatchAuthorityEvidenceHash)
            || !IsOptionalEvidenceReference(attempt.BeforeEvidenceId)
            || !IsOptionalEvidenceReference(attempt.AfterEvidenceId)
            || attempt.PreviousContentHash is not null && !IsCanonicalSha256(attempt.PreviousContentHash))
        {
            return "effect-attempt-evidence-invalid";
        }
        if (attempt.PreviousContentHash is null
                && (attempt.Payload.Phase != GovernedLoopEffectPhase.IntentPrepared
                    || attempt.DispatchAuthorityEvidenceHash is not null
                    || attempt.AfterEvidenceId is not null)
            || attempt.PreviousContentHash is not null
                && attempt.Payload.Phase == GovernedLoopEffectPhase.IntentPrepared
                && attempt.DispatchAuthorityEvidenceHash is null
            || attempt.Payload.Phase is not (GovernedLoopEffectPhase.IntentPrepared or GovernedLoopEffectPhase.DispatchNotStarted)
                && attempt.DispatchAuthorityEvidenceHash is null
            || attempt.AfterEvidenceId is not null
                && attempt.Payload.Phase is not (GovernedLoopEffectPhase.OutcomeObserved or GovernedLoopEffectPhase.Committed or GovernedLoopEffectPhase.ReconciliationRequired or GovernedLoopEffectPhase.Reconciled))
        {
            return "effect-attempt-authority-phase-invalid";
        }

        var expectedIntent = ComputeIntent(
            attempt.Binding,
            attempt.NodeId,
            attempt.NodeAttempt,
            attempt.Capability,
            attempt.Implementation,
            attempt.ActuatorOperationId,
            attempt.OperationDescriptorHash,
            attempt.Payload.EffectId,
            attempt.Payload.OperationId,
            attempt.Payload.EffectGeneration,
            attempt.InputFingerprint,
            attempt.TargetFingerprint,
            attempt.PreconditionEvidenceHash,
            attempt.AdmissionAuthorityEvidenceHash,
            attempt.BeforeEvidenceId);
        if (!string.Equals(expectedIntent, attempt.Payload.IntentHash, StringComparison.Ordinal))
        {
            return "effect-attempt-intent-hash-mismatch";
        }
        if (!requireHash)
        {
            return null;
        }
        if (!IsCanonicalSha256(attempt.ContentHash))
        {
            return "effect-attempt-content-hash-invalid";
        }

        var expectedContent = Compute(attempt);
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expectedContent), Encoding.ASCII.GetBytes(attempt.ContentHash))
            ? null
            : "effect-attempt-content-hash-mismatch";
    }

    private static void RequireCurrent(GovernedLoopEffectAttempt current)
    {
        ArgumentNullException.ThrowIfNull(current);
        var error = Validate(current);
        if (error is not null)
        {
            throw new ArgumentException(error, nameof(current));
        }
    }

    private static bool SameImmutableIntent(GovernedLoopEffectAttempt current, GovernedLoopEffectAttempt next)
        => Equals(current.Binding, next.Binding)
            && string.Equals(current.NodeId, next.NodeId, StringComparison.Ordinal)
            && current.NodeAttempt == next.NodeAttempt
            && Equals(current.Capability, next.Capability)
            && Equals(current.Implementation, next.Implementation)
            && string.Equals(current.ActuatorOperationId, next.ActuatorOperationId, StringComparison.Ordinal)
            && string.Equals(current.OperationDescriptorHash, next.OperationDescriptorHash, StringComparison.Ordinal)
            && string.Equals(current.InputFingerprint, next.InputFingerprint, StringComparison.Ordinal)
            && string.Equals(current.TargetFingerprint, next.TargetFingerprint, StringComparison.Ordinal)
            && string.Equals(current.PreconditionEvidenceHash, next.PreconditionEvidenceHash, StringComparison.Ordinal)
            && string.Equals(current.AdmissionAuthorityEvidenceHash, next.AdmissionAuthorityEvidenceHash, StringComparison.Ordinal)
            && string.Equals(current.BeforeEvidenceId, next.BeforeEvidenceId, StringComparison.Ordinal);

    private static bool SamePayloadExceptTime(GovernedLoopEffectPayload current, GovernedLoopEffectPayload next)
        => current.SchemaVersion == next.SchemaVersion
            && string.Equals(current.EffectId, next.EffectId, StringComparison.Ordinal)
            && string.Equals(current.OperationId, next.OperationId, StringComparison.Ordinal)
            && current.EffectGeneration == next.EffectGeneration
            && current.Origin == next.Origin
            && string.Equals(current.OriginNodeId, next.OriginNodeId, StringComparison.Ordinal)
            && string.Equals(current.IntentHash, next.IntentHash, StringComparison.Ordinal)
            && current.Phase == next.Phase
            && current.Outcome == next.Outcome
            && current.EvidenceStatus == next.EvidenceStatus
            && string.Equals(current.OutcomeEvidenceId, next.OutcomeEvidenceId, StringComparison.Ordinal)
            && string.Equals(current.ReconciliationEvidenceId, next.ReconciliationEvidenceId, StringComparison.Ordinal);

    private static bool IsProtocolTransition(GovernedLoopEffectPhase current, GovernedLoopEffectPhase next)
        => current switch
        {
            GovernedLoopEffectPhase.IntentPrepared => next is GovernedLoopEffectPhase.DispatchNotStarted or GovernedLoopEffectPhase.DispatchBoundaryReached,
            GovernedLoopEffectPhase.DispatchBoundaryReached => next is GovernedLoopEffectPhase.OutcomeObserved or GovernedLoopEffectPhase.ReconciliationRequired,
            GovernedLoopEffectPhase.OutcomeObserved => next is GovernedLoopEffectPhase.Committed or GovernedLoopEffectPhase.ReconciliationRequired,
            _ => false,
        };

    private static void AppendAttemptIdentity(IncrementalHash hash, string domain, GovernedLoopEffectAttempt attempt)
    {
        Append(hash, domain);
        Append(hash, attempt.SchemaVersion);
        AppendBinding(hash, attempt.Binding);
        Append(hash, attempt.NodeId);
        Append(hash, attempt.NodeAttempt);
        Append(hash, attempt.Capability.Id.Value);
        Append(hash, attempt.Capability.Version.Value);
        Append(hash, attempt.Capability.Hash.Value);
        Append(hash, attempt.Implementation.ProviderId.Value);
        Append(hash, attempt.Implementation.ImplementationId);
        Append(hash, attempt.ActuatorOperationId);
        Append(hash, attempt.OperationDescriptorHash);
        Append(hash, attempt.InputFingerprint);
        Append(hash, attempt.TargetFingerprint);
        Append(hash, attempt.PreconditionEvidenceHash);
        Append(hash, attempt.AdmissionAuthorityEvidenceHash);
        Append(hash, attempt.BeforeEvidenceId);
        Append(hash, attempt.Payload.EffectId);
        Append(hash, attempt.Payload.OperationId);
        Append(hash, attempt.Payload.EffectGeneration);
        Append(hash, attempt.Payload.IntentHash);
    }

    private static void AppendBinding(IncrementalHash hash, GovernedLoopExecutionBinding binding)
    {
        Append(hash, binding?.SchemaVersion ?? 0);
        Append(hash, binding?.RunId);
        Append(hash, binding?.Revision?.SchemaVersion ?? 0);
        Append(hash, binding?.Revision?.GraphId);
        Append(hash, binding?.Revision?.RevisionId);
        Append(hash, binding?.Revision?.ExecutableHash);
        Append(hash, binding?.ExecutionGeneration ?? 0);
    }

    private static bool IsIdentifier(string? value)
    {
        try
        {
            _ = GovernedLoopExecutionContractGuard.RequireIdentifier(value, nameof(value));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool IsOptionalEvidenceReference(string? value)
    {
        if (value is null)
        {
            return true;
        }
        try
        {
            _ = GovernedLoopExecutionContractGuard.RequireIdentifier(value, nameof(value), GovernedLoopExecutionLimits.MaxEvidenceReferenceCharacters);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool IsCanonicalSha256(string? value)
        => value is { Length: GovernedLoopExecutionLimits.Sha256HexCharacters }
            && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsUtcAtOrAfter(DateTimeOffset value, DateTimeOffset minimum)
        => value != default && value.Offset == TimeSpan.Zero && value >= minimum;

    private static void Append(IncrementalHash hash, string? value)
    {
        var bytes = value is null ? [] : Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, value is null ? -1 : bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private static void Append(IncrementalHash hash, int value)
        => Append(hash, value.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static void Append(IncrementalHash hash, long value)
        => Append(hash, value.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static void Append<TEnum>(IncrementalHash hash, TEnum value)
        where TEnum : struct, Enum
        => Append(hash, Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture));

    private static void Append(IncrementalHash hash, DateTimeOffset value)
        => Append(hash, value.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
}
