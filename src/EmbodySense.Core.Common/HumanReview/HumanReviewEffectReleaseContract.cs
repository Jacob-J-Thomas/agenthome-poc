using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.HumanReview.Models;
using EmbodySense.Core.Common.Loops.Execution.Effects;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;

namespace EmbodySense.Core.Common.HumanReview;

/// <summary>Creates, validates, hashes, copies, and compares value-free effect-release evidence without dispatching an effect or granting authority.</summary>
public static class HumanReviewEffectReleaseContract
{
    /// <summary>Creates the exact identity and preparation snapshot currently retained by an immutable effect attempt and its reviewed frontier binding.</summary>
    /// <remarks>This method binds evidence only. A caller must still re-read current authority and may never treat a returned snapshot as dispatch permission.</remarks>
    /// <exception cref="ArgumentException">Thrown when the binding, attempt, or observation time is not an exact supported schema-1 value.</exception>
    public static HumanReviewEffectCertaintySnapshot Create(HumanReviewBinding binding, GovernedLoopEffectAttempt attempt, DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(attempt);
        if (binding.EffectAttempt is null
            || !HumanReviewContractHash.MatchesBinding(binding)
            || binding.EffectAttempt.DispatchCertainty != HumanReviewEffectDispatchCertainty.NotDispatched
            || GovernedLoopEffectAttemptContract.Validate(attempt) is not null
            || !IsUtc(observedAtUtc)
            || observedAtUtc < attempt.Payload.UpdatedAtUtc)
        {
            throw new ArgumentException("A validated canonical effect attempt and trusted observation time are required.", nameof(attempt));
        }

        var identity = CreateIdentity(binding, attempt);
        var preparation = CreatePreparation(binding, attempt);
        var certainty = DeriveCertainty(attempt.Payload);
        var candidate = new HumanReviewEffectCertaintySnapshot(1, identity, preparation, attempt.DispatchAuthorityEvidenceHash, attempt.Payload.Phase, certainty, observedAtUtc, string.Empty);
        var snapshot = candidate with { SnapshotHash = ComputeSnapshot(candidate) };
        var error = Validate(snapshot);
        if (error is not null)
        {
            throw new ArgumentException(error, nameof(binding));
        }

        return snapshot;
    }

    /// <summary>Returns a bounded reason code when a snapshot is malformed, divergent, forward-versioned, or unsafe; otherwise returns <see langword="null"/>.</summary>
    public static string? Validate(HumanReviewEffectCertaintySnapshot? snapshot)
    {
        if (snapshot is null || snapshot.SchemaVersion != 1 || !IsUtc(snapshot.ObservedAtUtc) || snapshot.Identity is null || snapshot.Preparation is null)
        {
            return "effect-certainty-snapshot-invalid";
        }
        if (ValidateIdentity(snapshot.Identity, requireHash: true) is not null || ValidatePreparation(snapshot.Preparation, requireHash: true) is not null)
        {
            return "effect-certainty-snapshot-binding-invalid";
        }
        if (!string.Equals(snapshot.Identity.IntentHash, snapshot.Preparation.IntentHash, StringComparison.Ordinal)
            || snapshot.DispatchAuthorityEvidenceHash is not null && !IsHash(snapshot.DispatchAuthorityEvidenceHash)
            || !Enum.IsDefined(snapshot.Phase)
            || snapshot.Phase == GovernedLoopEffectPhase.Unknown
            || !Enum.IsDefined(snapshot.Certainty)
            || snapshot.Certainty == HumanReviewEffectCertainty.Unknown
            || !IsCertaintyCompatible(snapshot.Phase, snapshot.Certainty, snapshot.DispatchAuthorityEvidenceHash is not null)
            || !IsHash(snapshot.SnapshotHash))
        {
            return "effect-certainty-snapshot-posture-invalid";
        }

        return FixedEquals(ComputeSnapshot(snapshot), snapshot.SnapshotHash) ? null : "effect-certainty-snapshot-hash-mismatch";
    }

    /// <summary>Computes the canonical hash of an exact effect identity excluding its self-referential hash field.</summary>
    public static string ComputeIdentity(HumanReviewEffectAttemptIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return Compute("human-review-effect-attempt-identity-v1", builder => AppendIdentity(builder, identity, false));
    }

    /// <summary>Computes the canonical hash of an exact value-free preparation fingerprint excluding its self-referential hash field.</summary>
    public static string ComputePreparation(HumanReviewEffectPreparationFingerprint preparation)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        return Compute("human-review-effect-preparation-v1", builder => AppendPreparation(builder, preparation, false));
    }

    /// <summary>Computes the canonical hash of a current effect-certainty snapshot excluding its self-referential hash field.</summary>
    public static string ComputeSnapshot(HumanReviewEffectCertaintySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return Compute("human-review-effect-certainty-snapshot-v1", builder => AppendSnapshot(builder, snapshot, false));
    }

    /// <summary>Returns whether two snapshots are exact replays, name separate attempts, or reuse an attempt identity with divergent safe evidence.</summary>
    public static HumanReviewEffectSnapshotReplayDisposition ClassifyReplay(HumanReviewEffectCertaintySnapshot? retained, HumanReviewEffectCertaintySnapshot? proposed)
    {
        if (!TryCapture(retained, out var retainedSnapshot, out _) || retainedSnapshot is null
            || !TryCapture(proposed, out var proposedSnapshot, out _) || proposedSnapshot is null)
        {
            return HumanReviewEffectSnapshotReplayDisposition.Invalid;
        }
        if (!string.Equals(retainedSnapshot.Identity.IdentityHash, proposedSnapshot.Identity.IdentityHash, StringComparison.Ordinal))
        {
            return HumanReviewEffectSnapshotReplayDisposition.New;
        }

        return string.Equals(retainedSnapshot.SnapshotHash, proposedSnapshot.SnapshotHash, StringComparison.Ordinal) && Equals(retainedSnapshot, proposedSnapshot)
            ? HumanReviewEffectSnapshotReplayDisposition.ExactReplay
            : HumanReviewEffectSnapshotReplayDisposition.DivergentReuse;
    }

    /// <summary>Captures an independently validated exact identity and preparation expectation for a read-only certainty query.</summary>
    /// <remarks>The captured values remain expectation evidence only. They do not prove current certainty, grant authority, or permit a dispatch.</remarks>
    public static bool TryCaptureExpectation(
        HumanReviewEffectAttemptIdentity? sourceIdentity,
        HumanReviewEffectPreparationFingerprint? sourcePreparation,
        out HumanReviewEffectAttemptIdentity? identity,
        out HumanReviewEffectPreparationFingerprint? preparation,
        out string? reasonCode)
    {
        identity = null;
        preparation = null;
        reasonCode = "effect-certainty-query-invalid";
        try
        {
            if (sourceIdentity is null || sourcePreparation is null)
            {
                return false;
            }
            var copiedIdentity = sourceIdentity with { };
            var copiedPreparation = sourcePreparation with { };
            if (ValidateIdentity(copiedIdentity, requireHash: true) is not null
                || ValidatePreparation(copiedPreparation, requireHash: true) is not null
                || !string.Equals(copiedIdentity.IntentHash, copiedPreparation.IntentHash, StringComparison.Ordinal))
            {
                return false;
            }
            identity = copiedIdentity;
            preparation = copiedPreparation;
            reasonCode = null;
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or NullReferenceException or IndexOutOfRangeException)
        {
            reasonCode = "effect-certainty-query-unstable";
            return false;
        }
    }

    /// <summary>Creates a detached validated copy, returning <see langword="false"/> when the source changes during capture or is invalid.</summary>
    public static bool TryCapture(HumanReviewEffectCertaintySnapshot? source, out HumanReviewEffectCertaintySnapshot? snapshot, out string? reasonCode)
    {
        snapshot = null;
        reasonCode = "effect-certainty-snapshot-invalid";
        try
        {
            if (source is null)
            {
                return false;
            }
            var copied = source with { Identity = source.Identity is null ? null! : source.Identity with { }, Preparation = source.Preparation is null ? null! : source.Preparation with { } };
            reasonCode = Validate(copied);
            if (reasonCode is not null)
            {
                return false;
            }
            snapshot = copied;
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or NullReferenceException or IndexOutOfRangeException)
        {
            reasonCode = "effect-certainty-snapshot-unstable";
            return false;
        }
    }

    /// <summary>Creates the exact stable attempt identity needed to bind a pre-dispatch review to one canonical effect attempt.</summary>
    /// <remarks>This detached identity contains only identifiers and hashes. It does not itself grant authority or prove a review has been admitted.</remarks>
    /// <exception cref="ArgumentException">Thrown when the supplied binding and effect attempt do not name the same exact supported coordinates.</exception>
    public static HumanReviewEffectAttemptIdentity CreateIdentity(HumanReviewBinding binding, GovernedLoopEffectAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(attempt);
        if (GovernedLoopEffectAttemptContract.Validate(attempt) is not null)
        {
            throw new ArgumentException("A validated canonical effect attempt is required.", nameof(attempt));
        }
        if (!MatchesReviewCoordinates(binding, attempt))
        {
            throw new ArgumentException("The review binding and effect attempt must name the exact same run, revision, node, and attempt coordinates.", nameof(binding));
        }
        var identity = new HumanReviewEffectAttemptIdentity(
            1, binding.RunId, binding.GraphId, binding.RevisionId, binding.RevisionHash, binding.FrontierId, binding.FrontierVersion, binding.FrontierHash,
            binding.NodeId, binding.ActivationOrdinal, binding.VisitOrdinal, binding.Attempt, attempt.Payload.EffectId, attempt.Payload.OperationId,
            attempt.Payload.EffectGeneration, attempt.ActuatorOperationId, attempt.Capability.Id.Value, attempt.Capability.Version.Value, attempt.Capability.Hash.Value,
            attempt.Implementation.ProviderId.Value, attempt.Implementation.ImplementationId, attempt.Payload.IntentHash, string.Empty);
        var identityError = ValidateIdentity(identity, requireHash: false);
        if (identityError is not null)
        {
            throw new ArgumentException(identityError, nameof(binding));
        }
        return identity with { IdentityHash = ComputeIdentity(identity) };
    }

    /// <summary>Creates the value-free preparation fingerprint that a pre-dispatch review must retain before a continuation can later prove no preparation drift.</summary>
    /// <remarks>Callers use this before constructing <see cref="HumanReviewEffectAttemptBinding"/>; a supplied existing binding is then required to exactly match the result.</remarks>
    /// <exception cref="ArgumentException">Thrown when the binding or attempt contains unsafe preparation evidence, or an existing reviewed binding diverges.</exception>
    public static HumanReviewEffectPreparationFingerprint CreatePreparation(HumanReviewBinding binding, GovernedLoopEffectAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(attempt);
        if (GovernedLoopEffectAttemptContract.Validate(attempt) is not null || !MatchesReviewCoordinates(binding, attempt))
        {
            throw new ArgumentException("A validated canonical effect attempt at the exact reviewed coordinates is required.", nameof(attempt));
        }
        var preparation = new HumanReviewEffectPreparationFingerprint(
            1, attempt.Payload.IntentHash, attempt.OperationDescriptorHash, attempt.InputFingerprint, attempt.TargetFingerprint, attempt.PreconditionEvidenceHash,
            binding.TargetHash, binding.PreconditionHash, binding.PayloadHash, attempt.BeforeEvidenceId, attempt.AdmissionAuthorityEvidenceHash, string.Empty);
        if (ValidatePreparation(preparation, requireHash: false) is not null)
        {
            throw new ArgumentException("The review binding or effect attempt contains invalid preparation evidence.", nameof(binding));
        }
        var hashed = preparation with { PreparationHash = ComputePreparation(preparation) };
        if (binding.EffectAttempt is { } reviewed && (!string.Equals(reviewed.EffectAttemptId, attempt.Payload.EffectId, StringComparison.Ordinal)
            || !string.Equals(reviewed.OperationId, attempt.Payload.OperationId, StringComparison.Ordinal)
            || reviewed.EffectGeneration != attempt.Payload.EffectGeneration
            || !string.Equals(reviewed.IntentHash, attempt.Payload.IntentHash, StringComparison.Ordinal)
            || !string.Equals(reviewed.PreparationHash, hashed.PreparationHash, StringComparison.Ordinal)))
        {
            throw new ArgumentException("The reviewed effect binding must exactly match the canonical effect preparation.", nameof(binding));
        }
        return hashed;
    }

    private static string? ValidateIdentity(HumanReviewEffectAttemptIdentity identity, bool requireHash)
    {
        if (identity.SchemaVersion != 1
            || !Id(identity.RunId) || !Id(identity.GraphId) || !Id(identity.RevisionId) || !Id(identity.FrontierId) || !Id(identity.NodeId) || !Id(identity.EffectId)
            || !Id(identity.OperationId) || !Path(identity.ActuatorOperationId, GovernedLoopEffectAttemptContractLimits.MaxOperationIdCharacters)
            || !CapabilityId.TryParse(identity.CapabilityId, out _, out _) || !CapabilityVersion.TryParse(identity.CapabilityVersion, out _, out _)
            || !CapabilityProviderId.TryParse(identity.ProviderId, out _, out _) || !Path(identity.ImplementationId, CapabilityContractLimits.MaxImplementationIdCharacters)
            || !IsHash(identity.RevisionHash) || !IsHash(identity.FrontierHash) || !CapabilityDescriptorHash.TryParse(identity.CapabilityDescriptorHash, out _, out _) || !IsHash(identity.IntentHash)
            || identity.FrontierVersion is < 1 or > HumanReviewContractLimits.MaxVersion
            || identity.NodeAttempt is < 1 or > HumanReviewContractLimits.MaxNodeAttempt
            || identity.EffectGeneration is < 1 or > HumanReviewContractLimits.MaxVersion
            || !ExactlyOneCoordinate(identity.ActivationOrdinal, identity.VisitOrdinal)
            || !IsOrdinal(identity.ActivationOrdinal) || !IsOrdinal(identity.VisitOrdinal)
            || requireHash && !IsHash(identity.IdentityHash))
        {
            return "effect-attempt-identity-invalid";
        }
        return !requireHash || FixedEquals(ComputeIdentity(identity), identity.IdentityHash) ? null : "effect-attempt-identity-hash-mismatch";
    }

    private static string? ValidatePreparation(HumanReviewEffectPreparationFingerprint preparation, bool requireHash)
    {
        if (preparation.SchemaVersion != 1
            || !IsHash(preparation.IntentHash) || !IsHash(preparation.OperationDescriptorHash) || !IsHash(preparation.InputFingerprint) || !IsHash(preparation.TargetFingerprint)
            || preparation.PreconditionEvidenceHash is not null && !IsHash(preparation.PreconditionEvidenceHash)
            || !IsHash(preparation.ReviewTargetHash) || !IsHash(preparation.ReviewPreconditionHash) || !IsHash(preparation.ReviewPayloadHash)
            || preparation.BeforeEvidenceId is not null && !Id(preparation.BeforeEvidenceId)
            || !IsHash(preparation.AdmissionAuthorityEvidenceHash) || requireHash && !IsHash(preparation.PreparationHash))
        {
            return "effect-preparation-invalid";
        }
        return !requireHash || FixedEquals(ComputePreparation(preparation), preparation.PreparationHash) ? null : "effect-preparation-hash-mismatch";
    }

    private static bool MatchesReviewCoordinates(HumanReviewBinding binding, GovernedLoopEffectAttempt attempt)
        => binding.SchemaVersion == 1
            && string.Equals(binding.RunId, attempt.Binding.RunId, StringComparison.Ordinal)
            && string.Equals(binding.GraphId, attempt.Binding.Revision.GraphId, StringComparison.Ordinal)
            && string.Equals(binding.RevisionId, attempt.Binding.Revision.RevisionId, StringComparison.Ordinal)
            && string.Equals(binding.RevisionHash, attempt.Binding.Revision.ExecutableHash, StringComparison.Ordinal)
            && string.Equals(binding.NodeId, attempt.NodeId, StringComparison.Ordinal)
            && binding.Attempt == attempt.NodeAttempt;

    private static HumanReviewEffectCertainty DeriveCertainty(GovernedLoopEffectPayload payload)
        => payload.Phase switch
        {
            GovernedLoopEffectPhase.IntentPrepared or GovernedLoopEffectPhase.DispatchNotStarted => HumanReviewEffectCertainty.NotStarted,
            GovernedLoopEffectPhase.DispatchBoundaryReached => HumanReviewEffectCertainty.Dispatched,
            GovernedLoopEffectPhase.OutcomeObserved when payload.Outcome is GovernedLoopEffectOutcome.Succeeded or GovernedLoopEffectOutcome.Failed && payload.EvidenceStatus == GovernedLoopEffectEvidenceStatus.Complete => HumanReviewEffectCertainty.Conclusive,
            GovernedLoopEffectPhase.Committed or GovernedLoopEffectPhase.Reconciled => HumanReviewEffectCertainty.Terminal,
            GovernedLoopEffectPhase.OutcomeObserved or GovernedLoopEffectPhase.ReconciliationRequired => HumanReviewEffectCertainty.Ambiguous,
            _ => HumanReviewEffectCertainty.Unknown,
        };

    private static bool IsCertaintyCompatible(GovernedLoopEffectPhase phase, HumanReviewEffectCertainty certainty, bool hasDispatchAuthority)
        => certainty switch
        {
            HumanReviewEffectCertainty.NotStarted => phase is GovernedLoopEffectPhase.IntentPrepared or GovernedLoopEffectPhase.DispatchNotStarted,
            HumanReviewEffectCertainty.Dispatched => phase == GovernedLoopEffectPhase.DispatchBoundaryReached && hasDispatchAuthority,
            HumanReviewEffectCertainty.Conclusive => phase == GovernedLoopEffectPhase.OutcomeObserved && hasDispatchAuthority,
            HumanReviewEffectCertainty.Ambiguous => phase is GovernedLoopEffectPhase.OutcomeObserved or GovernedLoopEffectPhase.ReconciliationRequired && hasDispatchAuthority,
            HumanReviewEffectCertainty.Terminal => phase is GovernedLoopEffectPhase.Committed or GovernedLoopEffectPhase.Reconciled && hasDispatchAuthority,
            _ => false,
        };

    private static string Compute(string domain, Action<StringBuilder> append)
    {
        var builder = new StringBuilder(1024);
        Append(builder, domain);
        append(builder);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    private static void AppendSnapshot(StringBuilder builder, HumanReviewEffectCertaintySnapshot snapshot, bool includeHash)
    {
        Append(builder, snapshot.SchemaVersion); AppendIdentity(builder, snapshot.Identity, true); AppendPreparation(builder, snapshot.Preparation, true);
        Append(builder, snapshot.DispatchAuthorityEvidenceHash); Append(builder, (int)snapshot.Phase); Append(builder, (int)snapshot.Certainty); Append(builder, snapshot.ObservedAtUtc);
        if (includeHash) Append(builder, snapshot.SnapshotHash);
    }

    private static void AppendIdentity(StringBuilder builder, HumanReviewEffectAttemptIdentity identity, bool includeHash)
    {
        Append(builder, identity.SchemaVersion); Append(builder, identity.RunId); Append(builder, identity.GraphId); Append(builder, identity.RevisionId); Append(builder, identity.RevisionHash);
        Append(builder, identity.FrontierId); Append(builder, identity.FrontierVersion); Append(builder, identity.FrontierHash); Append(builder, identity.NodeId); Append(builder, identity.ActivationOrdinal); Append(builder, identity.VisitOrdinal); Append(builder, identity.NodeAttempt);
        Append(builder, identity.EffectId); Append(builder, identity.OperationId); Append(builder, identity.EffectGeneration); Append(builder, identity.ActuatorOperationId); Append(builder, identity.CapabilityId); Append(builder, identity.CapabilityVersion); Append(builder, identity.CapabilityDescriptorHash); Append(builder, identity.ProviderId); Append(builder, identity.ImplementationId); Append(builder, identity.IntentHash);
        if (includeHash) Append(builder, identity.IdentityHash);
    }

    private static void AppendPreparation(StringBuilder builder, HumanReviewEffectPreparationFingerprint preparation, bool includeHash)
    {
        Append(builder, preparation.SchemaVersion); Append(builder, preparation.IntentHash); Append(builder, preparation.OperationDescriptorHash); Append(builder, preparation.InputFingerprint); Append(builder, preparation.TargetFingerprint); Append(builder, preparation.PreconditionEvidenceHash);
        Append(builder, preparation.ReviewTargetHash); Append(builder, preparation.ReviewPreconditionHash); Append(builder, preparation.ReviewPayloadHash); Append(builder, preparation.BeforeEvidenceId); Append(builder, preparation.AdmissionAuthorityEvidenceHash);
        if (includeHash) Append(builder, preparation.PreparationHash);
    }

    private static bool Id(string? value) => HumanReviewIdentifier.IsValid(value);
    private static bool Path(string? value, int maxCharacters) => CapabilityIdentifierRules.IsPath(value, maxCharacters);
    private static bool IsHash(string? value) => HumanReviewContractHash.IsSha256(value);
    private static bool IsUtc(DateTimeOffset value) => value != default && value.Offset == TimeSpan.Zero;
    private static bool IsOrdinal(int? value) => value is null || value is >= 0 and <= HumanReviewContractLimits.MaxActivationOrVisit;
    private static bool ExactlyOneCoordinate(int? activation, int? visit) => (activation is null) != (visit is null);
    private static bool FixedEquals(string left, string right) => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(left), Encoding.ASCII.GetBytes(right));
    private static void Append(StringBuilder builder, DateTimeOffset value) => Append(builder, value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
    private static void Append(StringBuilder builder, int? value) => Append(builder, value?.ToString(CultureInfo.InvariantCulture));
    private static void Append(StringBuilder builder, int value) => Append(builder, value.ToString(CultureInfo.InvariantCulture));
    private static void Append(StringBuilder builder, long value) => Append(builder, value.ToString(CultureInfo.InvariantCulture));
    private static void Append(StringBuilder builder, string? value) { if (value is null) { builder.Append("-1:"); return; } var normalized = value.Normalize(NormalizationForm.FormC); builder.Append(Encoding.UTF8.GetByteCount(normalized).ToString(CultureInfo.InvariantCulture)); builder.Append(':'); builder.Append(normalized); }
}
