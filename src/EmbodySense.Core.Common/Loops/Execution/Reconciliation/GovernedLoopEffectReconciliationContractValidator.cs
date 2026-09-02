using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Loops.Execution.Effects;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Core.Common.Loops.Execution.Reconciliation;

/// <summary>Validates bounded immutable effect-reconciliation evidence without consulting external state.</summary>
public static class GovernedLoopEffectReconciliationContractValidator
{
    /// <summary>Validates an exact reconciliation binding.</summary>
    public static GovernedLoopEffectReconciliationValidationResult Validate(GovernedLoopEffectReconciliationBinding? binding)
    {
        var errors = new List<GovernedLoopEffectReconciliationValidationError>();
        ValidateBinding(binding, "$binding", errors, requireHash: true);
        return Result(errors);
    }

    /// <summary>Validates versioned actuator reconciliation contract metadata.</summary>
    public static GovernedLoopEffectReconciliationValidationResult Validate(GovernedLoopEffectReconciliationContractMetadata? metadata)
    {
        var errors = new List<GovernedLoopEffectReconciliationValidationError>();
        ValidateMetadata(metadata, "$contractMetadata", errors, requireHash: true);
        return Result(errors);
    }

    /// <summary>Validates one registered evidence source structurally.</summary>
    public static GovernedLoopEffectReconciliationValidationResult Validate(GovernedLoopEffectReconciliationEvidenceSource? source)
    {
        var errors = new List<GovernedLoopEffectReconciliationValidationError>();
        ValidateSource(source, "$evidenceSource", errors, requireHash: true);
        return Result(errors);
    }

    /// <summary>Validates one value-free observation structurally.</summary>
    public static GovernedLoopEffectReconciliationValidationResult Validate(GovernedLoopEffectReconciliationObservation? observation)
    {
        var errors = new List<GovernedLoopEffectReconciliationValidationError>();
        ValidateObservation(observation, "$observation", errors, requireHash: true);
        return Result(errors);
    }

    /// <summary>Validates one authoritative assessment structurally.</summary>
    public static GovernedLoopEffectReconciliationValidationResult Validate(GovernedLoopEffectReconciliationAssessment? assessment)
    {
        var errors = new List<GovernedLoopEffectReconciliationValidationError>();
        ValidateAssessment(assessment, "$assessment", errors, requireHash: true);
        return Result(errors);
    }

    /// <summary>Validates one authoritative disposition structurally.</summary>
    public static GovernedLoopEffectReconciliationValidationResult Validate(GovernedLoopEffectReconciliationDisposition? disposition)
    {
        var errors = new List<GovernedLoopEffectReconciliationValidationError>();
        ValidateDisposition(disposition, "$disposition", errors, requireHash: true);
        return Result(errors);
    }

    /// <summary>Validates one optional accepted resolution structurally.</summary>
    public static GovernedLoopEffectReconciliationValidationResult Validate(GovernedLoopEffectReconciliationResolution? resolution)
    {
        var errors = new List<GovernedLoopEffectReconciliationValidationError>();
        ValidateResolution(resolution, "$resolution", errors, requireHash: true);
        return Result(errors);
    }

    /// <summary>Validates one complete case, including all anti-replay bindings and proof semantics.</summary>
    public static GovernedLoopEffectReconciliationValidationResult Validate(GovernedLoopEffectReconciliationCase? reconciliationCase)
    {
        var errors = new List<GovernedLoopEffectReconciliationValidationError>();
        ValidateCase(reconciliationCase, errors, requireHash: true);
        return Result(errors);
    }

    /// <summary>Validates a case against the exact current authoritative reconciliation-required attempt.</summary>
    public static GovernedLoopEffectReconciliationValidationResult Validate(GovernedLoopEffectReconciliationCase? reconciliationCase, GovernedLoopEffectAttempt? currentAttempt)
    {
        var errors = new List<GovernedLoopEffectReconciliationValidationError>();
        ValidateCase(reconciliationCase, errors, requireHash: true);
        if (reconciliationCase is not null && errors.Count == 0)
        {
            ValidateCurrentAttempt(reconciliationCase, currentAttempt, errors);
        }

        return Result(errors);
    }

    /// <summary>Validates one direct immutable case-version successor.</summary>
    public static GovernedLoopEffectReconciliationValidationResult ValidateTransition(GovernedLoopEffectReconciliationCase? current, GovernedLoopEffectReconciliationCase? next)
    {
        var errors = new List<GovernedLoopEffectReconciliationValidationError>();
        ValidateCase(current, errors, requireHash: true, "$current");
        ValidateCase(next, errors, requireHash: true, "$next");
        if (current is null || next is null || errors.Count != 0)
        {
            return Result(errors);
        }

        if (!string.Equals(current.CaseId, next.CaseId, StringComparison.Ordinal)
            || !string.Equals(current.Binding.ContentHash, next.Binding.ContentHash, StringComparison.Ordinal)
            || !string.Equals(current.ContractMetadata.ContentHash, next.ContractMetadata.ContentHash, StringComparison.Ordinal)
            || current.OpenedAtUtc != next.OpenedAtUtc)
        {
            Add(errors, GovernedLoopEffectReconciliationValidationErrorCode.BindingMismatch, "$next.binding");
        }
        if (current.CaseVersion == long.MaxValue || next.CaseVersion != current.CaseVersion + 1 || !FixedHashEquals(current.ContentHash, next.PreviousContentHash))
        {
            Add(errors, GovernedLoopEffectReconciliationValidationErrorCode.IntegrityMismatch, "$next.previousContentHash");
        }
        if (next.UpdatedAtUtc < current.UpdatedAtUtc
            || !IsPrefix(current.EvidenceSources.Select(value => value.ContentHash), next.EvidenceSources.Select(value => value.ContentHash))
            || !IsPrefix(current.ObservationHistory.Select(value => value.ContentHash), next.ObservationHistory.Select(value => value.ContentHash))
            || !IsPrefix(current.AssessmentHistory.Select(value => value.ContentHash), next.AssessmentHistory.Select(value => value.ContentHash))
            || !IsPrefix(current.CaseReceiptHashes, next.CaseReceiptHashes))
        {
            Add(errors, GovernedLoopEffectReconciliationValidationErrorCode.InvalidComposition, "$next");
        }
        if (current.Disposition is not null
            && (!FixedHashEquals(current.CurrentAssessmentHash, next.CurrentAssessmentHash)
                || next.Disposition is null
                || !FixedHashEquals(current.Disposition.ContentHash, next.Disposition.ContentHash)))
        {
            Add(errors, GovernedLoopEffectReconciliationValidationErrorCode.IllegalDisposition, "$next.disposition");
        }
        if (current.Resolution is not null && (next.Resolution is null || !FixedHashEquals(current.Resolution.ContentHash, next.Resolution.ContentHash)))
        {
            Add(errors, GovernedLoopEffectReconciliationValidationErrorCode.IllegalResolution, "$next.resolution");
        }

        return Result(errors);
    }

    internal static bool IsCanonicalSha256(string? value)
        => value is { Length: GovernedLoopEffectReconciliationContractLimits.Sha256HexCharacters }
            && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static void ThrowIfInvalidForHash(GovernedLoopEffectReconciliationBinding binding, string parameterName)
        => ThrowIfAny(errors => ValidateBinding(binding, "$binding", errors, requireHash: false), parameterName);

    internal static void ThrowIfInvalidForHash(GovernedLoopEffectReconciliationContractMetadata metadata, string parameterName)
        => ThrowIfAny(errors => ValidateMetadata(metadata, "$contractMetadata", errors, requireHash: false), parameterName);

    internal static void ThrowIfInvalidForHash(GovernedLoopEffectReconciliationEvidenceSource source, string parameterName)
        => ThrowIfAny(errors => ValidateSource(source, "$evidenceSource", errors, requireHash: false), parameterName);

    internal static void ThrowIfInvalidForHash(GovernedLoopEffectReconciliationObservation observation, string parameterName)
        => ThrowIfAny(errors => ValidateObservation(observation, "$observation", errors, requireHash: false), parameterName);

    internal static void ThrowIfInvalidForHash(GovernedLoopEffectReconciliationAssessment assessment, string parameterName)
        => ThrowIfAny(errors => ValidateAssessment(assessment, "$assessment", errors, requireHash: false), parameterName);

    internal static void ThrowIfInvalidForHash(GovernedLoopEffectReconciliationDisposition disposition, string parameterName)
        => ThrowIfAny(errors => ValidateDisposition(disposition, "$disposition", errors, requireHash: false), parameterName);

    internal static void ThrowIfInvalidForHash(GovernedLoopEffectReconciliationResolution resolution, string parameterName)
        => ThrowIfAny(errors => ValidateResolution(resolution, "$resolution", errors, requireHash: false), parameterName);

    internal static void ThrowIfInvalidForHash(GovernedLoopEffectReconciliationCase reconciliationCase, string parameterName)
        => ThrowIfAny(errors => ValidateCase(reconciliationCase, errors, requireHash: false), parameterName);

    private static void ValidateCase(GovernedLoopEffectReconciliationCase? value, List<GovernedLoopEffectReconciliationValidationError> errors, bool requireHash, string path = "$case")
    {
        if (value is null)
        {
            Add(errors, GovernedLoopEffectReconciliationValidationErrorCode.Required, path);
            return;
        }

        ValidateSchema(value.SchemaVersion, $"{path}.schemaVersion", errors);
        ValidateIdentifier(value.CaseId, $"{path}.caseId", errors);
        if (value.CaseVersion < 1)
        {
            Add(errors, GovernedLoopEffectReconciliationValidationErrorCode.LimitExceeded, $"{path}.caseVersion");
        }
        if (value.CaseVersion == 1 && value.PreviousContentHash is not null
            || value.CaseVersion > 1 && !IsCanonicalSha256(value.PreviousContentHash))
        {
            Add(errors, GovernedLoopEffectReconciliationValidationErrorCode.InvalidHash, $"{path}.previousContentHash");
        }

        ValidateBinding(value.Binding, $"{path}.binding", errors, requireHash: true);
        ValidateMetadata(value.ContractMetadata, $"{path}.contractMetadata", errors, requireHash: true);
        ValidateUtc(value.OpenedAtUtc, $"{path}.openedAtUtc", errors);
        ValidateUtc(value.UpdatedAtUtc, $"{path}.updatedAtUtc", errors);
        if (value.UpdatedAtUtc < value.OpenedAtUtc)
        {
            Add(errors, GovernedLoopEffectReconciliationValidationErrorCode.InvalidTimestamp, $"{path}.updatedAtUtc");
        }

        if (value.EvidenceSources is null || value.ObservationHistory is null || value.AssessmentHistory is null || value.CaseReceiptHashes is null)
        {
            Add(errors, GovernedLoopEffectReconciliationValidationErrorCode.Required, path);
            return;
        }
        ValidateCount(value.EvidenceSources.Count, GovernedLoopEffectReconciliationContractLimits.MaxEvidenceSources, $"{path}.evidenceSources", errors);
        ValidateCount(value.ObservationHistory.Count, GovernedLoopEffectReconciliationContractLimits.MaxObservations, $"{path}.observationHistory", errors);
        ValidateCount(value.AssessmentHistory.Count, GovernedLoopEffectReconciliationContractLimits.MaxAssessments, $"{path}.assessmentHistory", errors);
        ValidateCount(value.CaseReceiptHashes.Count, GovernedLoopEffectReconciliationContractLimits.MaxCaseReceipts, $"{path}.caseReceiptHashes", errors);
        ValidateCanonical(value.EvidenceSources.Select(item => item?.SourceId), $"{path}.evidenceSources", errors);
        ValidateCanonical(value.ObservationHistory.Select(item => item?.ObservationId), $"{path}.observationHistory", errors);
        ValidateCanonical(value.AssessmentHistory.Select(item => item?.AssessmentId), $"{path}.assessmentHistory", errors);
        ValidateCanonical(value.CaseReceiptHashes, $"{path}.caseReceiptHashes", errors);

        for (var index = 0; index < value.EvidenceSources.Count; index++)
        {
            ValidateSource(value.EvidenceSources[index], $"{path}.evidenceSources[{index}]", errors, requireHash: true);
        }
        for (var index = 0; index < value.ObservationHistory.Count; index++)
        {
            ValidateObservation(value.ObservationHistory[index], $"{path}.observationHistory[{index}]", errors, requireHash: true);
        }
        for (var index = 0; index < value.AssessmentHistory.Count; index++)
        {
            ValidateAssessment(value.AssessmentHistory[index], $"{path}.assessmentHistory[{index}]", errors, requireHash: true);
        }
        foreach (var receipt in value.CaseReceiptHashes)
        {
            if (!IsCanonicalSha256(receipt))
            {
                Add(errors, GovernedLoopEffectReconciliationValidationErrorCode.InvalidHash, $"{path}.caseReceiptHashes");
            }
        }

        if (value.EvidenceSources.Any(source => source is not null && source.RegisteredAtUtc > value.UpdatedAtUtc)
            || value.ObservationHistory.Any(observation => observation is not null && observation.RecordedAtUtc > value.UpdatedAtUtc)
            || value.AssessmentHistory.Any(assessment => assessment is not null && assessment.AssessedAtUtc > value.UpdatedAtUtc)
            || value.Disposition is not null && value.Disposition.DisposedAtUtc > value.UpdatedAtUtc
            || value.Resolution is not null && value.Resolution.ResolvedAtUtc > value.UpdatedAtUtc)
        {
            Add(errors, GovernedLoopEffectReconciliationValidationErrorCode.InvalidTimestamp, $"{path}.updatedAtUtc");
        }

        ValidateCaseBindings(value, errors, path);
        ValidateAssessments(value, errors, path);
        ValidateDispositionAndResolution(value, errors, path);
        if (requireHash && !GovernedLoopEffectReconciliationContractHash.Matches(value))
        {
            Add(errors, GovernedLoopEffectReconciliationValidationErrorCode.IntegrityMismatch, $"{path}.contentHash");
        }
    }

    private static void ValidateBinding(GovernedLoopEffectReconciliationBinding? value, string path, List<GovernedLoopEffectReconciliationValidationError> errors, bool requireHash)
    {
        if (value is null)
        {
            Add(errors, GovernedLoopEffectReconciliationValidationErrorCode.Required, path);
            return;
        }

        ValidateSchema(value.SchemaVersion, $"{path}.schemaVersion", errors);
        if (!ContextualRoleWorkspaceId.IsValid(value.WorkspaceId))
        {
            Add(errors, GovernedLoopEffectReconciliationValidationErrorCode.InvalidIdentity, $"{path}.workspaceId");
        }
        if (value.Execution is null || !GovernedLoopExecutionValidator.Validate(value.Execution).IsValid)
        {
            Add(errors, GovernedLoopEffectReconciliationValidationErrorCode.BindingMismatch, $"{path}.execution");
        }
        ValidateIdentifier(value.NodeId, $"{path}.nodeId", errors);
        if (value.ActivationOrdinal is < 0 or >= GovernedLoopExecutionLimits.MaxFrontierNodes
            || value.VisitOrdinal is < 1 or > GovernedLoopExecutionLimits.MaxNodeVisits
            || value.NodeAttempt is < 1 or > GovernedLoopExecutionLimits.MaxNodeAttempt
            || value.EffectGeneration is < 1 or > GovernedLoopExecutionLimits.MaxVersion)
        {
            Add(errors, GovernedLoopEffectReconciliationValidationErrorCode.LimitExceeded, path);
        }
        ValidateIdentifier(value.EffectId, $"{path}.effectId", errors);
        ValidateIdentifier(value.OperationId, $"{path}.operationId", errors);
        ValidateHash(value.IntentHash, $"{path}.intentHash", errors);
        ValidateHash(value.CurrentAttemptHash, $"{path}.currentAttemptHash", errors);
        if (requireHash && !GovernedLoopEffectReconciliationContractHash.Matches(value))
        {
            Add(errors, GovernedLoopEffectReconciliationValidationErrorCode.IntegrityMismatch, $"{path}.contentHash");
        }
    }

    private static void ValidateMetadata(GovernedLoopEffectReconciliationContractMetadata? value, string path, List<GovernedLoopEffectReconciliationValidationError> errors, bool requireHash)
    {
        if (value is null)
        {
            Add(errors, GovernedLoopEffectReconciliationValidationErrorCode.Required, path);
            return;
        }

        ValidateSchema(value.SchemaVersion, $"{path}.schemaVersion", errors);
        ValidateIdentifier(value.ContractId, $"{path}.contractId", errors);
        ValidateIdentifier(value.ProbeContractId, $"{path}.probeContractId", errors);
        if (value.ContractVersion < 1 || value.ProbeContractVersion < 1)
        {
            Add(errors, GovernedLoopEffectReconciliationValidationErrorCode.LimitExceeded, path);
        }
        if (!IsCapability(value.Capability, value.Implementation))
        {
            Add(errors, GovernedLoopEffectReconciliationValidationErrorCode.InvalidIdentity, $"{path}.capability");
        }
        if (!CapabilityIdentifierRules.IsPath(value.ActuatorOperationId, GovernedLoopEffectReconciliationContractLimits.MaxIdentifierCharacters))
        {
            Add(errors, GovernedLoopEffectReconciliationValidationErrorCode.InvalidIdentity, $"{path}.actuatorOperationId");
        }
        ValidateHash(value.OperationDescriptorHash, $"{path}.operationDescriptorHash", errors);
        ValidateHash(value.ProbeContractHash, $"{path}.probeContractHash", errors);
        if (requireHash && !GovernedLoopEffectReconciliationContractHash.Matches(value))
        {
            Add(errors, GovernedLoopEffectReconciliationValidationErrorCode.IntegrityMismatch, $"{path}.contentHash");
        }
    }

    private static void ValidateSource(GovernedLoopEffectReconciliationEvidenceSource? value, string path, List<GovernedLoopEffectReconciliationValidationError> errors, bool requireHash)
    {
        if (value is null)
        {
            Add(errors, GovernedLoopEffectReconciliationValidationErrorCode.Required, path);
            return;
        }

        ValidateSchema(value.SchemaVersion, $"{path}.schemaVersion", errors);
        ValidateIdentifier(value.CaseId, $"{path}.caseId", errors);
        ValidateHash(value.BindingHash, $"{path}.bindingHash", errors);
        ValidateIdentifier(value.SourceId, $"{path}.sourceId", errors);
        ValidateIdentifier(value.ReconciliationContractId, $"{path}.reconciliationContractId", errors);
        if (value.ReconciliationContractVersion < 1)
        {
            Add(errors, GovernedLoopEffectReconciliationValidationErrorCode.LimitExceeded, $"{path}.reconciliationContractVersion");
        }
        if (value.Kind == GovernedLoopEffectReconciliationEvidenceSourceKind.Unknown || !Enum.IsDefined(value.Kind)
            || value.ReliabilityPosture == GovernedLoopEffectReconciliationReliabilityPosture.Unknown || !Enum.IsDefined(value.ReliabilityPosture))
        {
            Add(errors, GovernedLoopEffectReconciliationValidationErrorCode.InvalidEnumeration, path);
        }
        ValidateHash(value.ReconciliationContractHash, $"{path}.reconciliationContractHash", errors);
        ValidateHash(value.RegistrationEvidenceHash, $"{path}.registrationEvidenceHash", errors);
        ValidateUtc(value.RegisteredAtUtc, $"{path}.registeredAtUtc", errors);
        if (value.RetiredAtUtc is { } retiredAtUtc && (!IsUtc(retiredAtUtc) || retiredAtUtc < value.RegisteredAtUtc))
        {
            Add(errors, GovernedLoopEffectReconciliationValidationErrorCode.InvalidTimestamp, $"{path}.retiredAtUtc");
        }
        if (value.Kind == GovernedLoopEffectReconciliationEvidenceSourceKind.Informational && value.ReliabilityPosture == GovernedLoopEffectReconciliationReliabilityPosture.Authoritative)
        {
            Add(errors, GovernedLoopEffectReconciliationValidationErrorCode.InvalidComposition, $"{path}.reliabilityPosture");
        }
        if (requireHash && !GovernedLoopEffectReconciliationContractHash.Matches(value))
        {
            Add(errors, GovernedLoopEffectReconciliationValidationErrorCode.IntegrityMismatch, $"{path}.contentHash");
        }
    }

    private static void ValidateObservation(GovernedLoopEffectReconciliationObservation? value, string path, List<GovernedLoopEffectReconciliationValidationError> errors, bool requireHash)
    {
        if (value is null)
        {
            Add(errors, GovernedLoopEffectReconciliationValidationErrorCode.Required, path);
            return;
        }

        ValidateSchema(value.SchemaVersion, $"{path}.schemaVersion", errors);
        ValidateIdentifier(value.CaseId, $"{path}.caseId", errors);
        ValidateHash(value.BindingHash, $"{path}.bindingHash", errors);
        ValidateIdentifier(value.ObservationId, $"{path}.observationId", errors);
        ValidateIdentifier(value.SourceId, $"{path}.sourceId", errors);
        ValidateHash(value.SourceRegistrationHash, $"{path}.sourceRegistrationHash", errors);
        if (value.Kind == GovernedLoopEffectReconciliationObservationKind.Unknown || !Enum.IsDefined(value.Kind)
            || !Enum.IsDefined(value.ObservedOutcome)
            || value.ReliabilityPosture == GovernedLoopEffectReconciliationReliabilityPosture.Unknown || !Enum.IsDefined(value.ReliabilityPosture))
        {
            Add(errors, GovernedLoopEffectReconciliationValidationErrorCode.InvalidEnumeration, path);
        }
        ValidateOptionalIdentifier(value.EvidenceReference, $"{path}.evidenceReference", errors);
        ValidateOptionalHash(value.EvidenceHash, $"{path}.evidenceHash", errors);
        ValidateUtc(value.RecordedAtUtc, $"{path}.recordedAtUtc", errors);
        ValidateOptionalText(value.SafeSummary, GovernedLoopEffectReconciliationContractLimits.MaxSummaryCharacters, $"{path}.safeSummary", errors);
        if (value.ObservedAtUtc is { } observedAtUtc && (!IsUtc(observedAtUtc) || observedAtUtc > value.RecordedAtUtc))
        {
            Add(errors, GovernedLoopEffectReconciliationValidationErrorCode.InvalidTimestamp, $"{path}.observedAtUtc");
        }

        var exactEvidence = value.Kind == GovernedLoopEffectReconciliationObservationKind.Evidence
            && GovernedLoopEffectReconciliationStateMatrix.IsSupported(value.ObservedOutcome)
            && value.EvidenceReference is not null
            && value.EvidenceHash is not null
            && value.ObservedAtUtc is not null;
        if (value.Kind == GovernedLoopEffectReconciliationObservationKind.Evidence && !exactEvidence
            || value.Kind is GovernedLoopEffectReconciliationObservationKind.Missing or GovernedLoopEffectReconciliationObservationKind.TimedOut or GovernedLoopEffectReconciliationObservationKind.Cancelled
                && (value.ObservedOutcome != GovernedLoopEffectReconciliationObservedOutcome.Unknown || value.EvidenceReference is not null || value.EvidenceHash is not null || value.ObservedAtUtc is not null))
        {
            Add(errors, GovernedLoopEffectReconciliationValidationErrorCode.InvalidComposition, path);
        }
        if (requireHash && !GovernedLoopEffectReconciliationContractHash.Matches(value))
        {
            Add(errors, GovernedLoopEffectReconciliationValidationErrorCode.IntegrityMismatch, $"{path}.contentHash");
        }
    }

    private static void ValidateAssessment(GovernedLoopEffectReconciliationAssessment? value, string path, List<GovernedLoopEffectReconciliationValidationError> errors, bool requireHash)
    {
        if (value is null)
        {
            Add(errors, GovernedLoopEffectReconciliationValidationErrorCode.Required, path);
            return;
        }

        ValidateSchema(value.SchemaVersion, $"{path}.schemaVersion", errors);
        ValidateIdentifier(value.CaseId, $"{path}.caseId", errors);
        ValidateHash(value.BindingHash, $"{path}.bindingHash", errors);
        ValidateIdentifier(value.AssessmentId, $"{path}.assessmentId", errors);
        if (!GovernedLoopEffectReconciliationStateMatrix.IsSupported(value.Kind))
        {
            Add(errors, GovernedLoopEffectReconciliationValidationErrorCode.InvalidEnumeration, $"{path}.kind");
        }
        if (value.ObservationHashes is null)
        {
            Add(errors, GovernedLoopEffectReconciliationValidationErrorCode.Required, $"{path}.observationHashes");
        }
        else
        {
            ValidateCount(value.ObservationHashes.Count, GovernedLoopEffectReconciliationContractLimits.MaxObservationReferences, $"{path}.observationHashes", errors);
            ValidateCanonical(value.ObservationHashes, $"{path}.observationHashes", errors);
            foreach (var hash in value.ObservationHashes)
            {
                ValidateHash(hash, $"{path}.observationHashes", errors);
            }
            if (value.Kind != GovernedLoopEffectReconciliationAssessmentKind.Inconclusive && value.ObservationHashes.Count == 0)
            {
                Add(errors, GovernedLoopEffectReconciliationValidationErrorCode.InvalidComposition, $"{path}.observationHashes");
            }
        }
        ValidateHash(value.AuthorityEvidenceHash, $"{path}.authorityEvidenceHash", errors);
        ValidateUtc(value.AssessedAtUtc, $"{path}.assessedAtUtc", errors);
        ValidateOptionalText(value.SafeDetail, GovernedLoopEffectReconciliationContractLimits.MaxDetailCharacters, $"{path}.safeDetail", errors);
        if (requireHash && !GovernedLoopEffectReconciliationContractHash.Matches(value))
        {
            Add(errors, GovernedLoopEffectReconciliationValidationErrorCode.IntegrityMismatch, $"{path}.contentHash");
        }
    }

    private static void ValidateDisposition(GovernedLoopEffectReconciliationDisposition? value, string path, List<GovernedLoopEffectReconciliationValidationError> errors, bool requireHash)
    {
        if (value is null)
        {
            Add(errors, GovernedLoopEffectReconciliationValidationErrorCode.Required, path);
            return;
        }

        ValidateSchema(value.SchemaVersion, $"{path}.schemaVersion", errors);
        ValidateIdentifier(value.CaseId, $"{path}.caseId", errors);
        ValidateHash(value.BindingHash, $"{path}.bindingHash", errors);
        ValidateIdentifier(value.DispositionId, $"{path}.dispositionId", errors);
        if (!GovernedLoopEffectReconciliationStateMatrix.IsSupported(value.Kind))
        {
            Add(errors, GovernedLoopEffectReconciliationValidationErrorCode.InvalidEnumeration, $"{path}.kind");
        }
        ValidateHash(value.AssessmentHash, $"{path}.assessmentHash", errors);
        ValidateHash(value.AuthorityEvidenceHash, $"{path}.authorityEvidenceHash", errors);
        ValidateUtc(value.DisposedAtUtc, $"{path}.disposedAtUtc", errors);
        ValidateOptionalText(value.SafeDetail, GovernedLoopEffectReconciliationContractLimits.MaxDetailCharacters, $"{path}.safeDetail", errors);
        if (requireHash && !GovernedLoopEffectReconciliationContractHash.Matches(value))
        {
            Add(errors, GovernedLoopEffectReconciliationValidationErrorCode.IntegrityMismatch, $"{path}.contentHash");
        }
    }

    private static void ValidateResolution(GovernedLoopEffectReconciliationResolution? value, string path, List<GovernedLoopEffectReconciliationValidationError> errors, bool requireHash)
    {
        if (value is null)
        {
            Add(errors, GovernedLoopEffectReconciliationValidationErrorCode.Required, path);
            return;
        }

        ValidateSchema(value.SchemaVersion, $"{path}.schemaVersion", errors);
        ValidateIdentifier(value.CaseId, $"{path}.caseId", errors);
        ValidateHash(value.BindingHash, $"{path}.bindingHash", errors);
        ValidateIdentifier(value.ResolutionId, $"{path}.resolutionId", errors);
        ValidateHash(value.AssessmentHash, $"{path}.assessmentHash", errors);
        ValidateHash(value.DispositionHash, $"{path}.dispositionHash", errors);
        if (value.Outcome is not (GovernedLoopEffectOutcome.NotApplied or GovernedLoopEffectOutcome.Succeeded or GovernedLoopEffectOutcome.Failed))
        {
            Add(errors, GovernedLoopEffectReconciliationValidationErrorCode.InvalidEnumeration, $"{path}.outcome");
        }
        ValidateOptionalIdentifier(value.OutcomeEvidenceId, $"{path}.outcomeEvidenceId", errors);
        ValidateOptionalHash(value.OutcomeEvidenceHash, $"{path}.outcomeEvidenceHash", errors);
        if (value.Outcome == GovernedLoopEffectOutcome.NotApplied
            ? value.OutcomeEvidenceId is not null || value.OutcomeEvidenceHash is not null
            : value.OutcomeEvidenceId is null || value.OutcomeEvidenceHash is null)
        {
            Add(errors, GovernedLoopEffectReconciliationValidationErrorCode.InvalidComposition, path);
        }
        ValidateHash(value.AuthorityEvidenceHash, $"{path}.authorityEvidenceHash", errors);
        ValidateUtc(value.ResolvedAtUtc, $"{path}.resolvedAtUtc", errors);
        ValidateOptionalText(value.SafeDetail, GovernedLoopEffectReconciliationContractLimits.MaxDetailCharacters, $"{path}.safeDetail", errors);
        if (requireHash && !GovernedLoopEffectReconciliationContractHash.Matches(value))
        {
            Add(errors, GovernedLoopEffectReconciliationValidationErrorCode.IntegrityMismatch, $"{path}.contentHash");
        }
    }

    private static void ValidateCaseBindings(GovernedLoopEffectReconciliationCase value, List<GovernedLoopEffectReconciliationValidationError> errors, string path)
    {
        var bindingHash = value.Binding?.ContentHash;
        var metadata = value.ContractMetadata;
        for (var index = 0; index < value.EvidenceSources.Count; index++)
        {
            var source = value.EvidenceSources[index];
            if (source is null
                || !string.Equals(source.CaseId, value.CaseId, StringComparison.Ordinal)
                || !FixedHashEquals(source.BindingHash, bindingHash)
                || metadata is null
                || !string.Equals(source.ReconciliationContractId, metadata.ContractId, StringComparison.Ordinal)
                || source.ReconciliationContractVersion != metadata.ContractVersion
                || !FixedHashEquals(source.ReconciliationContractHash, metadata.ContentHash))
            {
                Add(errors, GovernedLoopEffectReconciliationValidationErrorCode.BindingMismatch, $"{path}.evidenceSources[{index}]");
            }
        }
        for (var index = 0; index < value.ObservationHistory.Count; index++)
        {
            var observation = value.ObservationHistory[index];
            if (observation is null || !string.Equals(observation.CaseId, value.CaseId, StringComparison.Ordinal) || !FixedHashEquals(observation.BindingHash, bindingHash))
            {
                Add(errors, GovernedLoopEffectReconciliationValidationErrorCode.BindingMismatch, $"{path}.observationHistory[{index}]");
                continue;
            }
            var source = value.EvidenceSources.FirstOrDefault(candidate => candidate is not null && string.Equals(candidate.SourceId, observation.SourceId, StringComparison.Ordinal));
            if (source is null
                || !FixedHashEquals(source.ContentHash, observation.SourceRegistrationHash)
                || source.ReliabilityPosture != observation.ReliabilityPosture
                || observation.RecordedAtUtc < source.RegisteredAtUtc
                || source.RetiredAtUtc is { } retiredAtUtc && observation.RecordedAtUtc > retiredAtUtc)
            {
                Add(errors, GovernedLoopEffectReconciliationValidationErrorCode.BindingMismatch, $"{path}.observationHistory[{index}].sourceId");
            }
        }
        for (var index = 0; index < value.AssessmentHistory.Count; index++)
        {
            var assessment = value.AssessmentHistory[index];
            if (assessment is null || !string.Equals(assessment.CaseId, value.CaseId, StringComparison.Ordinal) || !FixedHashEquals(assessment.BindingHash, bindingHash))
            {
                Add(errors, GovernedLoopEffectReconciliationValidationErrorCode.BindingMismatch, $"{path}.assessmentHistory[{index}]");
            }
        }
        if (value.Disposition is not null && (!string.Equals(value.Disposition.CaseId, value.CaseId, StringComparison.Ordinal) || !FixedHashEquals(value.Disposition.BindingHash, bindingHash)))
        {
            Add(errors, GovernedLoopEffectReconciliationValidationErrorCode.BindingMismatch, $"{path}.disposition");
        }
        if (value.Resolution is not null && (!string.Equals(value.Resolution.CaseId, value.CaseId, StringComparison.Ordinal) || !FixedHashEquals(value.Resolution.BindingHash, bindingHash)))
        {
            Add(errors, GovernedLoopEffectReconciliationValidationErrorCode.BindingMismatch, $"{path}.resolution");
        }
    }

    private static void ValidateAssessments(GovernedLoopEffectReconciliationCase value, List<GovernedLoopEffectReconciliationValidationError> errors, string path)
    {
        var observations = value.ObservationHistory
            .Where(item => item is not null && IsCanonicalSha256(item.ContentHash))
            .GroupBy(item => item.ContentHash, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        for (var index = 0; index < value.AssessmentHistory.Count; index++)
        {
            var assessment = value.AssessmentHistory[index];
            if (assessment is null || assessment.ObservationHashes is null)
            {
                continue;
            }
            var referenced = new List<GovernedLoopEffectReconciliationObservation>();
            foreach (var hash in assessment.ObservationHashes)
            {
                if (!observations.TryGetValue(hash, out var observation))
                {
                    Add(errors, GovernedLoopEffectReconciliationValidationErrorCode.BindingMismatch, $"{path}.assessmentHistory[{index}].observationHashes");
                    continue;
                }
                referenced.Add(observation);
                if (observation.RecordedAtUtc > assessment.AssessedAtUtc)
                {
                    Add(errors, GovernedLoopEffectReconciliationValidationErrorCode.InvalidTimestamp, $"{path}.assessmentHistory[{index}].assessedAtUtc");
                }
            }

            var authoritativeOutcomes = referenced
                .Where(observation => IsFreshAuthoritative(value, observation))
                .Select(observation => observation.ObservedOutcome)
                .Distinct()
                .ToArray();
            var expectedKind = authoritativeOutcomes.Length switch
            {
                0 => GovernedLoopEffectReconciliationAssessmentKind.Inconclusive,
                > 1 => GovernedLoopEffectReconciliationAssessmentKind.Conflicting,
                _ => authoritativeOutcomes[0] switch
                {
                    GovernedLoopEffectReconciliationObservedOutcome.NotApplied => GovernedLoopEffectReconciliationAssessmentKind.ProvedNotApplied,
                    GovernedLoopEffectReconciliationObservedOutcome.AppliedSucceeded => GovernedLoopEffectReconciliationAssessmentKind.ProvedAppliedSucceeded,
                    GovernedLoopEffectReconciliationObservedOutcome.AppliedFailed => GovernedLoopEffectReconciliationAssessmentKind.ProvedAppliedFailed,
                    GovernedLoopEffectReconciliationObservedOutcome.AppliedOutcomeUnknown => GovernedLoopEffectReconciliationAssessmentKind.ProvedAppliedOutcomeUnknown,
                    _ => GovernedLoopEffectReconciliationAssessmentKind.Unknown
                }
            };
            if (assessment.Kind != expectedKind)
            {
                Add(errors, GovernedLoopEffectReconciliationValidationErrorCode.InvalidComposition, $"{path}.assessmentHistory[{index}]");
            }
        }

        if (value.AssessmentHistory.Count == 0)
        {
            if (value.CurrentAssessmentHash is not null || value.Disposition is not null || value.Resolution is not null)
            {
                Add(errors, GovernedLoopEffectReconciliationValidationErrorCode.IllegalDisposition, $"{path}.currentAssessmentHash");
            }
            return;
        }

        var latest = value.AssessmentHistory.Where(item => item is not null).OrderBy(item => item.AssessedAtUtc).ThenBy(item => item.AssessmentId, StringComparer.Ordinal).LastOrDefault();
        if (latest is null)
        {
            Add(errors, GovernedLoopEffectReconciliationValidationErrorCode.Required, $"{path}.assessmentHistory");
            return;
        }
        if (!FixedHashEquals(value.CurrentAssessmentHash, latest.ContentHash))
        {
            Add(errors, GovernedLoopEffectReconciliationValidationErrorCode.IllegalDisposition, $"{path}.currentAssessmentHash");
        }
    }

    private static void ValidateDispositionAndResolution(GovernedLoopEffectReconciliationCase value, List<GovernedLoopEffectReconciliationValidationError> errors, string path)
    {
        if (value.CurrentAssessmentHash is null)
        {
            if (value.Disposition is not null || value.Resolution is not null)
            {
                Add(errors, GovernedLoopEffectReconciliationValidationErrorCode.IllegalDisposition, $"{path}.disposition");
            }
            return;
        }

        var assessment = value.AssessmentHistory.FirstOrDefault(item => item is not null && FixedHashEquals(item.ContentHash, value.CurrentAssessmentHash));
        if (assessment is null)
        {
            return;
        }
        if (value.Disposition is null)
        {
            if (value.Resolution is not null)
            {
                Add(errors, GovernedLoopEffectReconciliationValidationErrorCode.IllegalResolution, $"{path}.resolution");
            }
            return;
        }

        ValidateDisposition(value.Disposition, $"{path}.disposition", errors, requireHash: true);
        if (!FixedHashEquals(value.Disposition.AssessmentHash, assessment.ContentHash)
            || !GovernedLoopEffectReconciliationStateMatrix.IsDispositionAllowed(assessment.Kind, value.Disposition.Kind)
            || value.Disposition.DisposedAtUtc < assessment.AssessedAtUtc)
        {
            Add(errors, GovernedLoopEffectReconciliationValidationErrorCode.IllegalDisposition, $"{path}.disposition");
        }
        if (value.Disposition.Kind == GovernedLoopEffectReconciliationDispositionKind.QuarantineUnresolved)
        {
            if (value.Resolution is not null)
            {
                Add(errors, GovernedLoopEffectReconciliationValidationErrorCode.IllegalResolution, $"{path}.resolution");
            }
            return;
        }
        if (value.Resolution is null)
        {
            return;
        }

        ValidateResolution(value.Resolution, $"{path}.resolution", errors, requireHash: true);
        if (!FixedHashEquals(value.Resolution.AssessmentHash, assessment.ContentHash)
            || !FixedHashEquals(value.Resolution.DispositionHash, value.Disposition.ContentHash)
            || !GovernedLoopEffectReconciliationStateMatrix.IsResolutionOutcomeAllowed(assessment.Kind, value.Resolution.Outcome)
            || value.Resolution.ResolvedAtUtc < value.Disposition.DisposedAtUtc)
        {
            Add(errors, GovernedLoopEffectReconciliationValidationErrorCode.IllegalResolution, $"{path}.resolution");
            return;
        }
        if (value.Resolution.Outcome is GovernedLoopEffectOutcome.Succeeded or GovernedLoopEffectOutcome.Failed)
        {
            var expected = value.Resolution.Outcome == GovernedLoopEffectOutcome.Succeeded
                ? GovernedLoopEffectReconciliationObservedOutcome.AppliedSucceeded
                : GovernedLoopEffectReconciliationObservedOutcome.AppliedFailed;
            var exactEvidence = assessment.ObservationHashes
                .Select(hash => value.ObservationHistory.SingleOrDefault(item => FixedHashEquals(item.ContentHash, hash)))
                .Any(observation => observation is not null
                    && IsFreshAuthoritative(value, observation)
                    && observation.ObservedOutcome == expected
                    && string.Equals(observation.EvidenceReference, value.Resolution.OutcomeEvidenceId, StringComparison.Ordinal)
                    && FixedHashEquals(observation.EvidenceHash, value.Resolution.OutcomeEvidenceHash));
            if (!exactEvidence)
            {
                Add(errors, GovernedLoopEffectReconciliationValidationErrorCode.IllegalResolution, $"{path}.resolution.outcomeEvidenceId");
            }
        }
    }

    private static bool IsFreshAuthoritative(GovernedLoopEffectReconciliationCase reconciliationCase, GovernedLoopEffectReconciliationObservation observation)
    {
        var source = reconciliationCase.EvidenceSources.FirstOrDefault(candidate => candidate is not null && string.Equals(candidate.SourceId, observation.SourceId, StringComparison.Ordinal));
        return source is not null
            && source.Kind == GovernedLoopEffectReconciliationEvidenceSourceKind.Authoritative
            && source.ReliabilityPosture == GovernedLoopEffectReconciliationReliabilityPosture.Authoritative
            && observation.ReliabilityPosture == GovernedLoopEffectReconciliationReliabilityPosture.Authoritative
            && observation.Kind == GovernedLoopEffectReconciliationObservationKind.Evidence
            && observation.ObservedAtUtc is { } observedAtUtc
            && observedAtUtc >= reconciliationCase.OpenedAtUtc
            && observation.EvidenceReference is not null
            && observation.EvidenceHash is not null
            && FixedHashEquals(source.ContentHash, observation.SourceRegistrationHash);
    }

    private static void ValidateCurrentAttempt(GovernedLoopEffectReconciliationCase reconciliationCase, GovernedLoopEffectAttempt? attempt, List<GovernedLoopEffectReconciliationValidationError> errors)
    {
        if (attempt is null || GovernedLoopEffectAttemptContract.Validate(attempt) is not null || attempt.Payload.Phase != GovernedLoopEffectPhase.ReconciliationRequired)
        {
            Add(errors, GovernedLoopEffectReconciliationValidationErrorCode.BindingMismatch, "$currentAttempt");
            return;
        }

        var binding = reconciliationCase.Binding;
        var metadata = reconciliationCase.ContractMetadata;
        if (!Equals(binding.Execution, attempt.Binding)
            || !string.Equals(binding.NodeId, attempt.NodeId, StringComparison.Ordinal)
            || binding.NodeAttempt != attempt.NodeAttempt
            || !string.Equals(binding.EffectId, attempt.Payload.EffectId, StringComparison.Ordinal)
            || !string.Equals(binding.OperationId, attempt.Payload.OperationId, StringComparison.Ordinal)
            || binding.EffectGeneration != attempt.Payload.EffectGeneration
            || !FixedHashEquals(binding.IntentHash, attempt.Payload.IntentHash)
            || !FixedHashEquals(binding.CurrentAttemptHash, attempt.ContentHash)
            || !Equals(metadata.Capability, attempt.Capability)
            || !Equals(metadata.Implementation, attempt.Implementation)
            || !string.Equals(metadata.ActuatorOperationId, attempt.ActuatorOperationId, StringComparison.Ordinal)
            || !FixedHashEquals(metadata.OperationDescriptorHash, attempt.OperationDescriptorHash)
            || reconciliationCase.OpenedAtUtc < attempt.Payload.UpdatedAtUtc)
        {
            Add(errors, GovernedLoopEffectReconciliationValidationErrorCode.BindingMismatch, "$currentAttempt");
        }
    }

    private static bool IsCapability(CapabilityDescriptorIdentity? capability, CapabilityImplementationIdentity? implementation)
        => capability?.Id is not null
            && capability.Version is not null
            && capability.Hash is not null
            && CapabilityId.TryParse(capability.Id.Value, out _, out _)
            && CapabilityVersion.TryParse(capability.Version.Value, out _, out _)
            && CapabilityDescriptorHash.TryParse(capability.Hash.Value, out _, out _)
            && implementation?.ProviderId is not null
            && CapabilityProviderId.TryParse(implementation.ProviderId.Value, out _, out _)
            && CapabilityIdentifierRules.IsPath(implementation.ImplementationId, CapabilityContractLimits.MaxImplementationIdCharacters);

    private static bool FixedHashEquals(string? left, string? right)
    {
        if (!IsCanonicalSha256(left) || !IsCanonicalSha256(right))
        {
            return false;
        }

        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(left!), System.Text.Encoding.ASCII.GetBytes(right!));
    }

    private static bool IsPrefix(IEnumerable<string> current, IEnumerable<string> next)
    {
        var currentValues = current.ToArray();
        var nextValues = next.ToArray();
        return nextValues.Length >= currentValues.Length && currentValues.SequenceEqual(nextValues.Take(currentValues.Length), StringComparer.Ordinal);
    }

    private static void ValidateSchema(int value, string path, List<GovernedLoopEffectReconciliationValidationError> errors)
    {
        if (value != GovernedLoopEffectReconciliationContractLimits.CurrentSchemaVersion)
        {
            Add(errors, GovernedLoopEffectReconciliationValidationErrorCode.UnsupportedSchemaVersion, path);
        }
    }

    private static void ValidateIdentifier(string? value, string path, List<GovernedLoopEffectReconciliationValidationError> errors)
    {
        try
        {
            _ = GovernedLoopExecutionContractGuard.RequireIdentifier(value, nameof(value), GovernedLoopEffectReconciliationContractLimits.MaxIdentifierCharacters);
        }
        catch (ArgumentException)
        {
            Add(errors, GovernedLoopEffectReconciliationValidationErrorCode.InvalidIdentity, path);
        }
    }

    private static void ValidateOptionalIdentifier(string? value, string path, List<GovernedLoopEffectReconciliationValidationError> errors)
    {
        if (value is not null)
        {
            ValidateIdentifier(value, path, errors);
        }
    }

    private static void ValidateHash(string? value, string path, List<GovernedLoopEffectReconciliationValidationError> errors)
    {
        if (!IsCanonicalSha256(value))
        {
            Add(errors, GovernedLoopEffectReconciliationValidationErrorCode.InvalidHash, path);
        }
    }

    private static void ValidateOptionalHash(string? value, string path, List<GovernedLoopEffectReconciliationValidationError> errors)
    {
        if (value is not null)
        {
            ValidateHash(value, path, errors);
        }
    }

    private static void ValidateUtc(DateTimeOffset value, string path, List<GovernedLoopEffectReconciliationValidationError> errors)
    {
        if (!IsUtc(value))
        {
            Add(errors, GovernedLoopEffectReconciliationValidationErrorCode.InvalidTimestamp, path);
        }
    }

    private static bool IsUtc(DateTimeOffset value) => value != default && value.Offset == TimeSpan.Zero;

    private static void ValidateOptionalText(string? value, int maximum, string path, List<GovernedLoopEffectReconciliationValidationError> errors)
    {
        if (value is not null && (value.Length is 0 || value.Length > maximum || value.Any(char.IsControl)))
        {
            Add(errors, GovernedLoopEffectReconciliationValidationErrorCode.LimitExceeded, path);
        }
    }

    private static void ValidateCount(int count, int maximum, string path, List<GovernedLoopEffectReconciliationValidationError> errors)
    {
        if (count > maximum)
        {
            Add(errors, GovernedLoopEffectReconciliationValidationErrorCode.LimitExceeded, path);
        }
    }

    private static void ValidateCanonical(IEnumerable<string?> values, string path, List<GovernedLoopEffectReconciliationValidationError> errors)
    {
        string? prior = null;
        foreach (var value in values)
        {
            if (value is null || prior is not null && StringComparer.Ordinal.Compare(prior, value) >= 0)
            {
                Add(errors, GovernedLoopEffectReconciliationValidationErrorCode.NonCanonicalOrder, path);
                return;
            }
            prior = value;
        }
    }

    private static void ThrowIfAny(Action<List<GovernedLoopEffectReconciliationValidationError>> validate, string parameterName)
    {
        var errors = new List<GovernedLoopEffectReconciliationValidationError>();
        validate(errors);
        if (errors.Count != 0)
        {
            throw new ArgumentException($"Effect reconciliation is invalid at {errors[0].Path}.", parameterName);
        }
    }

    private static void Add(List<GovernedLoopEffectReconciliationValidationError> errors, GovernedLoopEffectReconciliationValidationErrorCode code, string path)
    {
        if (errors.Count < GovernedLoopEffectReconciliationContractLimits.MaxValidationErrors)
        {
            errors.Add(GovernedLoopEffectReconciliationValidationError.Create(code, path));
        }
    }

    private static GovernedLoopEffectReconciliationValidationResult Result(IEnumerable<GovernedLoopEffectReconciliationValidationError> errors)
        => GovernedLoopEffectReconciliationValidationResult.FromErrors(errors);
}
