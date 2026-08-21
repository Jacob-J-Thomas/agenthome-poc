using System.Globalization;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Execution.Sleep;
using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;
using EmbodySense.Core.Common.Loops.Execution.Wait.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Common.Loops.Execution.Wait;

/// <summary>Validates and admits bounded schema-1 Wait descriptors and retained continuation evidence without executing work.</summary>
public static class GovernedLoopWaitContractValidator
{
    /// <summary>Validates one closed Wait descriptor and its exact string parameter set.</summary>
    public static GovernedLoopWaitValidationResult ValidateDescriptor(
        GovernedLoopNodeDescriptor? descriptor,
        IReadOnlyDictionary<string, string>? parameters)
    {
        var errors = new List<GovernedLoopWaitValidationError>();
        ValidateDescriptor(descriptor, parameters, errors);
        return Result(errors);
    }

    /// <summary>Attempts to admit one exact Wait descriptor into its immutable typed condition.</summary>
    public static bool TryCreateCondition(
        GovernedLoopNodeDescriptor? descriptor,
        IReadOnlyDictionary<string, string>? parameters,
        out GovernedLoopWaitCondition? condition,
        out GovernedLoopWaitValidationResult validation)
    {
        var errors = new List<GovernedLoopWaitValidationError>();
        var parameter = ValidateDescriptor(descriptor, parameters, errors);
        validation = Result(errors);
        if (!validation.IsValid)
        {
            condition = null;
            return false;
        }

        var admittedValue = parameter!.Value.Value;
        condition = descriptor!.TypeId switch
        {
            GovernedLoopWaitVocabulary.Timestamp => GovernedLoopWaitContractHash.Apply(new GovernedLoopWaitCondition(
                GovernedLoopWaitCondition.CurrentSchemaVersion,
                descriptor,
                GovernedLoopWaitParameterKind.UtcTimestamp,
                ParseTimestamp(admittedValue),
                null,
                string.Empty)),
            GovernedLoopWaitVocabulary.AuthenticatedEvent => GovernedLoopWaitContractHash.Apply(new GovernedLoopWaitCondition(
                GovernedLoopWaitCondition.CurrentSchemaVersion,
                descriptor,
                GovernedLoopWaitParameterKind.AuthenticatedEventReference,
                null,
                admittedValue,
                string.Empty)),
            _ => throw new InvalidOperationException("A validated Wait descriptor must belong to the closed catalog.")
        };
        return true;
    }

    /// <summary>Validates one immutable typed Wait condition.</summary>
    public static GovernedLoopWaitValidationResult Validate(GovernedLoopWaitCondition? condition)
    {
        var errors = new List<GovernedLoopWaitValidationError>();
        ValidateCondition(condition, "$", errors);
        return Result(errors);
    }

    /// <summary>Validates one immutable parked Wait evidence value.</summary>
    public static GovernedLoopWaitValidationResult Validate(GovernedLoopWaitParkEvidence? evidence)
    {
        var errors = new List<GovernedLoopWaitValidationError>();
        ValidateParkEvidence(evidence, "$", errors);
        return Result(errors);
    }

    /// <summary>Validates one immutable Wait continuation evidence value.</summary>
    public static GovernedLoopWaitValidationResult Validate(GovernedLoopWaitContinuationEvidence? evidence)
    {
        var errors = new List<GovernedLoopWaitValidationError>();
        ValidateContinuationEvidence(evidence, "$", errors);
        return Result(errors);
    }

    /// <summary>Validates one bounded, hash-bound activation-scoped Wait evidence value.</summary>
    public static GovernedLoopWaitValidationResult Validate(GovernedLoopWaitExecutionEvidence? evidence)
    {
        var errors = new List<GovernedLoopWaitValidationError>();
        ValidateExecutionEvidence(evidence, "$", errors);
        return Result(errors);
    }

    /// <summary>Validates that one prepared continuation targets the exact parked checkpoint and contiguous frontier successor.</summary>
    public static GovernedLoopWaitValidationResult ValidateComposition(
        GovernedLoopWaitParkEvidence? park,
        GovernedLoopWaitContinuationEvidence? continuation)
    {
        var errors = new List<GovernedLoopWaitValidationError>();
        ValidateParkEvidence(park, "$.park", errors);
        ValidateContinuationEvidence(continuation, "$.continuation", errors);
        if (park is not null && continuation is not null && errors.Count == 0)
        {
            ValidateParkContinuationComposition(park, continuation, errors);
        }

        return Result(errors);
    }

    /// <summary>Validates the complete prepared-wake, frontier-continuation, and committed-wake evidence chain.</summary>
    public static GovernedLoopWaitValidationResult ValidateComposition(
        GovernedLoopWaitParkEvidence? park,
        GovernedLoopWaitContinuationEvidence? continuation,
        GovernedLoopWakeEvidence? committedWake)
    {
        var errors = new List<GovernedLoopWaitValidationError>();
        ValidateParkEvidence(park, "$.park", errors);
        ValidateContinuationEvidence(continuation, "$.continuation", errors);
        if (!GovernedLoopSleepContractValidator.Validate(committedWake).IsValid
            || committedWake?.Disposition != GovernedLoopWakeDisposition.Committed)
        {
            Add(errors, GovernedLoopWaitValidationErrorCode.InvalidComposition, "$.committedWake");
        }

        if (park is not null && continuation is not null && committedWake is not null && errors.Count == 0)
        {
            ValidateParkContinuationComposition(park, continuation, errors);
            if (!GovernedLoopSleepContractValidator.ValidateComposition(park.Checkpoint, committedWake).IsValid
                || !IsCommittedWakeSuccessor(continuation.PreparedWakeEvidence, committedWake))
            {
                Add(errors, GovernedLoopWaitValidationErrorCode.BindingMismatch, "$.committedWake");
            }

            if (!string.Equals(committedWake.ContinuationEvidenceHash, continuation.ContentHash, StringComparison.Ordinal))
            {
                Add(errors, GovernedLoopWaitValidationErrorCode.BindingMismatch, "$.committedWake.continuationEvidenceHash");
            }

            if (continuation.ResumedAtUtc > committedWake.RecordedAtUtc)
            {
                Add(errors, GovernedLoopWaitValidationErrorCode.InvalidTimestamp, "$.committedWake.recordedAtUtc");
            }
        }

        return Result(errors);
    }

    private static bool IsCommittedWakeSuccessor(
        GovernedLoopWakeEvidence prepared,
        GovernedLoopWakeEvidence committed)
    {
        if (GovernedLoopSleepContractValidator.ValidateTransition(prepared, committed).IsValid)
        {
            return true;
        }

        return prepared.Disposition == GovernedLoopWakeDisposition.Prepared
            && committed.Disposition == GovernedLoopWakeDisposition.Committed
            && committed.EvidenceVersion == prepared.EvidenceVersion + 2
            && string.Equals(prepared.Identity.ContentHash, committed.Identity.ContentHash, StringComparison.Ordinal)
            && string.Equals(prepared.Identity.WakeId, committed.Identity.WakeId, StringComparison.Ordinal)
            && string.Equals(prepared.ContinuationOperationId, committed.ContinuationOperationId, StringComparison.Ordinal)
            && committed.RecordedAtUtc >= prepared.RecordedAtUtc;
    }

    private static KeyValuePair<string, string>? ValidateDescriptor(
        GovernedLoopNodeDescriptor? descriptor,
        IReadOnlyDictionary<string, string>? parameters,
        List<GovernedLoopWaitValidationError> errors)
    {
        if (descriptor is null)
        {
            Add(errors, GovernedLoopWaitValidationErrorCode.Required, "$.descriptor");
        }
        else if (descriptor.Kind != GovernedLoopNodeKind.Wait
            || descriptor.Version != GovernedLoopWaitVocabulary.DescriptorVersion
            || !GovernedLoopWaitVocabulary.IsSupported(descriptor.TypeId))
        {
            Add(errors, GovernedLoopWaitValidationErrorCode.InvalidDescriptor, "$.descriptor");
        }

        if (parameters is null)
        {
            Add(errors, GovernedLoopWaitValidationErrorCode.Required, "$.parameters");
            return null;
        }

        if (descriptor is null || !GovernedLoopWaitVocabulary.IsSupported(descriptor.TypeId))
        {
            return null;
        }

        var expectedParameter = descriptor.TypeId == GovernedLoopWaitVocabulary.Timestamp
            ? GovernedLoopWaitVocabulary.DeadlineUtcParameter
            : GovernedLoopWaitVocabulary.EventReferenceParameter;
        if (!TryReadSoleParameter(parameters, out var parameter)
            || !string.Equals(parameter.Key, expectedParameter, StringComparison.Ordinal)
            || parameter.Value is null)
        {
            Add(errors, GovernedLoopWaitValidationErrorCode.InvalidParameter, "$.parameters");
            return null;
        }

        if (descriptor.TypeId == GovernedLoopWaitVocabulary.Timestamp)
        {
            if (!TryParseTimestamp(parameter.Value, out _))
            {
                Add(errors, GovernedLoopWaitValidationErrorCode.InvalidTimestamp, "$.parameters[deadline-utc]");
            }
        }
        else if (!CustomLoopArtifactIdentifier.IsValid(parameter.Value, GovernedLoopWaitContractLimits.MaxEventReferenceCharacters))
        {
            Add(errors, parameter.Value.Length > GovernedLoopWaitContractLimits.MaxEventReferenceCharacters
                ? GovernedLoopWaitValidationErrorCode.LimitExceeded
                : GovernedLoopWaitValidationErrorCode.InvalidIdentity, "$.parameters[event-reference]");
        }

        return parameter;
    }

    private static void ValidateCondition(
        GovernedLoopWaitCondition? condition,
        string path,
        List<GovernedLoopWaitValidationError> errors)
    {
        if (condition is null)
        {
            Add(errors, GovernedLoopWaitValidationErrorCode.Required, path);
            return;
        }

        var initialErrorCount = errors.Count;
        ValidateSchema(condition.SchemaVersion, $"{path}.schemaVersion", errors);
        if (condition.Descriptor is null
            || condition.Descriptor.Kind != GovernedLoopNodeKind.Wait
            || condition.Descriptor.Version != GovernedLoopWaitVocabulary.DescriptorVersion
            || !GovernedLoopWaitVocabulary.IsSupported(condition.Descriptor.TypeId))
        {
            Add(errors, GovernedLoopWaitValidationErrorCode.InvalidDescriptor, $"{path}.descriptor");
        }

        if (!Enum.IsDefined(condition.ParameterKind))
        {
            Add(errors, GovernedLoopWaitValidationErrorCode.InvalidParameter, $"{path}.parameterKind");
        }
        else if (condition.Descriptor is not null)
        {
            var timestampShape = condition.Descriptor.TypeId == GovernedLoopWaitVocabulary.Timestamp
                && condition.ParameterKind == GovernedLoopWaitParameterKind.UtcTimestamp
                && condition.WakeDeadlineUtc is { } deadlineUtc
                && IsUtc(deadlineUtc)
                && condition.AuthenticatedEventReference is null;
            var eventShape = condition.Descriptor.TypeId == GovernedLoopWaitVocabulary.AuthenticatedEvent
                && condition.ParameterKind == GovernedLoopWaitParameterKind.AuthenticatedEventReference
                && condition.WakeDeadlineUtc is null
                && CustomLoopArtifactIdentifier.IsValid(condition.AuthenticatedEventReference, GovernedLoopWaitContractLimits.MaxEventReferenceCharacters);
            if (!timestampShape && !eventShape)
            {
                Add(errors, GovernedLoopWaitValidationErrorCode.InvalidComposition, path);
            }
        }

        ValidateHash(condition.ContentHash, $"{path}.contentHash", errors);
        if (errors.Count == initialErrorCount && !GovernedLoopWaitContractHash.Matches(condition))
        {
            Add(errors, GovernedLoopWaitValidationErrorCode.IntegrityMismatch, $"{path}.contentHash");
        }
    }

    private static void ValidateParkEvidence(
        GovernedLoopWaitParkEvidence? evidence,
        string path,
        List<GovernedLoopWaitValidationError> errors)
    {
        if (evidence is null)
        {
            Add(errors, GovernedLoopWaitValidationErrorCode.Required, path);
            return;
        }

        var initialErrorCount = errors.Count;
        ValidateSchema(evidence.SchemaVersion, $"{path}.schemaVersion", errors);
        ValidateCondition(evidence.Condition, $"{path}.condition", errors);
        if (!GovernedLoopSleepContractValidator.Validate(evidence.Checkpoint).IsValid)
        {
            Add(errors, GovernedLoopWaitValidationErrorCode.InvalidComposition, $"{path}.checkpoint");
        }

        if (!IsUtc(evidence.ParkedAtUtc))
        {
            Add(errors, GovernedLoopWaitValidationErrorCode.InvalidTimestamp, $"{path}.parkedAtUtc");
        }

        if (evidence.Condition is not null && evidence.Checkpoint is not null)
        {
            var exactTimestamp = evidence.Condition.ParameterKind == GovernedLoopWaitParameterKind.UtcTimestamp
                && evidence.Checkpoint.WakeMode == GovernedLoopWakeMode.Timestamp
                && evidence.Condition.WakeDeadlineUtc == evidence.Checkpoint.WakeDeadlineUtc
                && evidence.Checkpoint.AuthenticatedEventReference is null;
            var exactEvent = evidence.Condition.ParameterKind == GovernedLoopWaitParameterKind.AuthenticatedEventReference
                && evidence.Checkpoint.WakeMode == GovernedLoopWakeMode.AuthenticatedEvent
                && string.Equals(evidence.Condition.AuthenticatedEventReference, evidence.Checkpoint.AuthenticatedEventReference, StringComparison.Ordinal)
                && evidence.Checkpoint.WakeDeadlineUtc is null;
            if (!exactTimestamp && !exactEvent)
            {
                Add(errors, GovernedLoopWaitValidationErrorCode.BindingMismatch, $"{path}.checkpoint");
            }

            if (evidence.ParkedAtUtc > evidence.Checkpoint.PublishedAtUtc)
            {
                Add(errors, GovernedLoopWaitValidationErrorCode.InvalidTimestamp, $"{path}.parkedAtUtc");
            }
        }

        ValidateHash(evidence.ContentHash, $"{path}.contentHash", errors);
        if (errors.Count == initialErrorCount && !GovernedLoopWaitContractHash.Matches(evidence))
        {
            Add(errors, GovernedLoopWaitValidationErrorCode.IntegrityMismatch, $"{path}.contentHash");
        }
    }

    private static void ValidateContinuationEvidence(
        GovernedLoopWaitContinuationEvidence? evidence,
        string path,
        List<GovernedLoopWaitValidationError> errors)
    {
        if (evidence is null)
        {
            Add(errors, GovernedLoopWaitValidationErrorCode.Required, path);
            return;
        }

        var initialErrorCount = errors.Count;
        ValidateSchema(evidence.SchemaVersion, $"{path}.schemaVersion", errors);
        ValidateHash(evidence.ParkEvidenceHash, $"{path}.parkEvidenceHash", errors);
        if (!GovernedLoopSleepContractValidator.Validate(evidence.PreparedWakeEvidence).IsValid
            || evidence.PreparedWakeEvidence?.Disposition != GovernedLoopWakeDisposition.Prepared)
        {
            Add(errors, GovernedLoopWaitValidationErrorCode.InvalidComposition, $"{path}.preparedWakeEvidence");
        }

        if (evidence.PreResumeFrontierVersion is < 1 or > GovernedLoopWaitContractLimits.MaxVersion)
        {
            Add(errors, GovernedLoopWaitValidationErrorCode.LimitExceeded, $"{path}.preResumeFrontierVersion");
        }

        ValidateHash(evidence.PreResumeFrontierHash, $"{path}.preResumeFrontierHash", errors);
        if (evidence.ResumedFrontierVersion is < 1 or > GovernedLoopWaitContractLimits.MaxVersion)
        {
            Add(errors, GovernedLoopWaitValidationErrorCode.LimitExceeded, $"{path}.resumedFrontierVersion");
        }
        else if (evidence.PreResumeFrontierVersion == GovernedLoopWaitContractLimits.MaxVersion
            || evidence.ResumedFrontierVersion != evidence.PreResumeFrontierVersion + 1)
        {
            Add(errors, GovernedLoopWaitValidationErrorCode.InvalidSuccessorVersion, $"{path}.resumedFrontierVersion");
        }

        ValidateHash(evidence.ResumedFrontierHash, $"{path}.resumedFrontierHash", errors);
        if (!IsUtc(evidence.ResumedAtUtc))
        {
            Add(errors, GovernedLoopWaitValidationErrorCode.InvalidTimestamp, $"{path}.resumedAtUtc");
        }

        if (evidence.PreparedWakeEvidence is not null
            && evidence.ResumedAtUtc < evidence.PreparedWakeEvidence.RecordedAtUtc)
        {
            Add(errors, GovernedLoopWaitValidationErrorCode.InvalidTimestamp, $"{path}.resumedAtUtc");
        }

        ValidateHash(evidence.ContentHash, $"{path}.contentHash", errors);
        if (errors.Count == initialErrorCount && !GovernedLoopWaitContractHash.Matches(evidence))
        {
            Add(errors, GovernedLoopWaitValidationErrorCode.IntegrityMismatch, $"{path}.contentHash");
        }
    }

    private static void ValidateExecutionEvidence(
        GovernedLoopWaitExecutionEvidence? evidence,
        string path,
        List<GovernedLoopWaitValidationError> errors)
    {
        if (evidence is null)
        {
            Add(errors, GovernedLoopWaitValidationErrorCode.Required, path);
            return;
        }

        var initialErrorCount = errors.Count;
        ValidateSchema(evidence.SchemaVersion, $"{path}.schemaVersion", errors);
        if (evidence.ActivationOrdinal is < 0 or >= GovernedLoopExecutionLimits.MaxFrontierNodes)
        {
            Add(errors, GovernedLoopWaitValidationErrorCode.LimitExceeded, $"{path}.activationOrdinal");
        }

        if (!CustomLoopArtifactIdentifier.IsValid(evidence.NodeId, GovernedLoopWaitContractLimits.MaxIdentifierCharacters))
        {
            Add(errors, GovernedLoopWaitValidationErrorCode.InvalidIdentity, $"{path}.nodeId");
        }

        if (evidence.NodeVisitOrdinal is < 1 or > GovernedLoopExecutionLimits.MaxNodeVisits)
        {
            Add(errors, GovernedLoopWaitValidationErrorCode.LimitExceeded, $"{path}.nodeVisitOrdinal");
        }

        var cycleShape = evidence.CycleId is null && evidence.CycleIteration is null
            || CustomLoopArtifactIdentifier.IsValid(evidence.CycleId, GovernedLoopWaitContractLimits.MaxIdentifierCharacters)
                && evidence.CycleIteration is >= 1 and <= GovernedLoopExecutionLimits.MaxCycleIterations;
        if (!cycleShape)
        {
            Add(errors, GovernedLoopWaitValidationErrorCode.InvalidComposition, $"{path}.cycleId");
        }

        if (evidence.WaitAttempt is < 1 or > GovernedLoopExecutionLimits.MaxNodeAttempt)
        {
            Add(errors, GovernedLoopWaitValidationErrorCode.LimitExceeded, $"{path}.waitAttempt");
        }

        if (!CustomLoopArtifactIdentifier.IsValid(evidence.WaitOperationId, GovernedLoopWaitContractLimits.MaxIdentifierCharacters))
        {
            Add(errors, GovernedLoopWaitValidationErrorCode.InvalidIdentity, $"{path}.waitOperationId");
        }

        ValidateCondition(evidence.Condition, $"{path}.condition", errors);
        if (!IsUtc(evidence.ParkedAtUtc))
        {
            Add(errors, GovernedLoopWaitValidationErrorCode.InvalidTimestamp, $"{path}.parkedAtUtc");
        }

        if (evidence.ParkedFrontierVersion is < 1 or > GovernedLoopWaitContractLimits.MaxVersion)
        {
            Add(errors, GovernedLoopWaitValidationErrorCode.LimitExceeded, $"{path}.parkedFrontierVersion");
        }

        ValidateHash(evidence.ParkedFrontierHash, $"{path}.parkedFrontierHash", errors);
        if (evidence.ParkEvidence is { } parkEvidence)
        {
            ValidateParkEvidence(parkEvidence, $"{path}.parkEvidence", errors);
            var binding = parkEvidence.Checkpoint?.Binding;
            if (!string.Equals(parkEvidence.Condition?.ContentHash, evidence.Condition?.ContentHash, StringComparison.Ordinal)
                || parkEvidence.ParkedAtUtc != evidence.ParkedAtUtc
                || binding is null
                || binding.FrontierVersion != evidence.ParkedFrontierVersion
                || !string.Equals(binding.FrontierHash, evidence.ParkedFrontierHash, StringComparison.Ordinal)
                || binding.ActivationOrdinal != evidence.ActivationOrdinal
                || !string.Equals(binding.NodeId, evidence.NodeId, StringComparison.Ordinal)
                || binding.NodeVisitOrdinal != evidence.NodeVisitOrdinal
                || !string.Equals(binding.CycleId, evidence.CycleId, StringComparison.Ordinal)
                || binding.CycleIteration != evidence.CycleIteration
                || binding.WaitAttempt != evidence.WaitAttempt
                || !string.Equals(binding.WaitOperationId, evidence.WaitOperationId, StringComparison.Ordinal))
            {
                Add(errors, GovernedLoopWaitValidationErrorCode.BindingMismatch, $"{path}.parkEvidence");
            }
        }

        if (evidence.ContinuationEvidence is { } continuationEvidence)
        {
            if (evidence.ParkEvidence is not { } retainedPark
                || !ValidateComposition(retainedPark, continuationEvidence).IsValid
                || continuationEvidence.PreResumeFrontierVersion < evidence.ParkedFrontierVersion
                || continuationEvidence.PreResumeFrontierVersion == evidence.ParkedFrontierVersion
                    && !string.Equals(continuationEvidence.PreResumeFrontierHash, evidence.ParkedFrontierHash, StringComparison.Ordinal))
            {
                Add(errors, GovernedLoopWaitValidationErrorCode.InvalidComposition, $"{path}.continuationEvidence");
            }
        }

        ValidateHash(evidence.ContentHash, $"{path}.contentHash", errors);
        if (errors.Count == initialErrorCount && !GovernedLoopWaitContractHash.Matches(evidence))
        {
            Add(errors, GovernedLoopWaitValidationErrorCode.IntegrityMismatch, $"{path}.contentHash");
        }
    }

    private static void ValidateParkContinuationComposition(
        GovernedLoopWaitParkEvidence park,
        GovernedLoopWaitContinuationEvidence continuation,
        List<GovernedLoopWaitValidationError> errors)
    {
        if (!string.Equals(park.ContentHash, continuation.ParkEvidenceHash, StringComparison.Ordinal))
        {
            Add(errors, GovernedLoopWaitValidationErrorCode.BindingMismatch, "$.continuation.parkEvidenceHash");
        }

        if (!GovernedLoopSleepContractValidator.ValidateComposition(park.Checkpoint, continuation.PreparedWakeEvidence).IsValid)
        {
            Add(errors, GovernedLoopWaitValidationErrorCode.BindingMismatch, "$.continuation.preparedWakeEvidence");
        }

        if (continuation.PreResumeFrontierVersion < park.Checkpoint.Binding.FrontierVersion
            || continuation.PreResumeFrontierVersion == park.Checkpoint.Binding.FrontierVersion
                && !string.Equals(continuation.PreResumeFrontierHash, park.Checkpoint.Binding.FrontierHash, StringComparison.Ordinal))
        {
            Add(errors, GovernedLoopWaitValidationErrorCode.BindingMismatch, "$.continuation.preResumeFrontierHash");
        }

        if (continuation.ResumedAtUtc < park.Checkpoint.PublishedAtUtc)
        {
            Add(errors, GovernedLoopWaitValidationErrorCode.InvalidTimestamp, "$.continuation.resumedAtUtc");
        }
    }

    private static bool TryReadSoleParameter(
        IReadOnlyDictionary<string, string> parameters,
        out KeyValuePair<string, string> parameter)
    {
        parameter = default;
        try
        {
            using var enumerator = parameters.GetEnumerator();
            if (!enumerator.MoveNext())
            {
                return false;
            }

            parameter = enumerator.Current;
            return !enumerator.MoveNext();
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            parameter = default;
            return false;
        }
    }

    private static DateTimeOffset ParseTimestamp(string value)
    {
        _ = TryParseTimestamp(value, out var timestamp);
        return timestamp;
    }

    private static bool TryParseTimestamp(string value, out DateTimeOffset timestamp)
        => DateTimeOffset.TryParseExact(
                value,
                GovernedLoopWaitVocabulary.CanonicalUtcTimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out timestamp)
            && IsUtc(timestamp)
            && string.Equals(
                timestamp.ToString(GovernedLoopWaitVocabulary.CanonicalUtcTimestampFormat, CultureInfo.InvariantCulture),
                value,
                StringComparison.Ordinal);

    private static bool IsUtc(DateTimeOffset value) => value != default && value.Offset == TimeSpan.Zero;

    private static void ValidateSchema(int schemaVersion, string path, List<GovernedLoopWaitValidationError> errors)
    {
        if (schemaVersion != GovernedLoopWaitContractLimits.CurrentSchemaVersion)
        {
            Add(errors, GovernedLoopWaitValidationErrorCode.UnsupportedSchemaVersion, path);
        }
    }

    private static void ValidateHash(string? value, string path, List<GovernedLoopWaitValidationError> errors)
    {
        if (value?.Length != GovernedLoopWaitContractLimits.Sha256HexCharacters
            || value.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            Add(errors, GovernedLoopWaitValidationErrorCode.InvalidHash, path);
        }
    }

    private static GovernedLoopWaitValidationResult Result(List<GovernedLoopWaitValidationError> errors)
        => GovernedLoopWaitValidationResult.FromErrors(errors);

    private static void Add(
        List<GovernedLoopWaitValidationError> errors,
        GovernedLoopWaitValidationErrorCode code,
        string path)
    {
        if (errors.Count < GovernedLoopWaitContractLimits.MaxValidationErrors)
        {
            errors.Add(GovernedLoopWaitValidationError.Create(code, path));
        }
    }
}
