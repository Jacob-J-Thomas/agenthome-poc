using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.Loops.Admission.Models;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions;

namespace EmbodySense.Core.Common.Loops.Admission;

/// <summary>Validates bounded schema-1 governed-loop admission intent, evidence, and terminal outcomes.</summary>
public static class GovernedLoopAdmissionValidator
{
    /// <summary>Validates one stable immutable admission intent.</summary>
    public static GovernedLoopAdmissionValidationResult Validate(GovernedLoopAdmissionIntent? intent)
        => Result(ValidateIntentStructure(intent));

    /// <summary>Validates successful evidence against its exact immutable admission intent.</summary>
    public static GovernedLoopAdmissionValidationResult Validate(GovernedLoopAdmissionEvidence? evidence, GovernedLoopAdmissionIntent? intent)
    {
        var errors = ValidateIntentStructure(intent);
        AddNested(errors, ValidateEvidenceStructure(evidence), "$.evidence");
        if (errors.Count == 0)
        {
            ValidateEvidenceBindings(evidence!, intent!, errors);
            if (!GovernedLoopAdmissionContractHash.Matches(evidence))
            {
                Add(errors, GovernedLoopAdmissionValidationErrorCode.HashMismatch, "$.evidence.contentHash");
            }
        }

        return Result(errors);
    }

    /// <summary>Validates one successful immutable admission receipt.</summary>
    public static GovernedLoopAdmissionValidationResult Validate(GovernedLoopAdmissionReceipt? receipt)
    {
        var errors = ValidateReceiptStructure(receipt);
        if (errors.Count == 0 && !GovernedLoopAdmissionContractHash.Matches(receipt))
        {
            Add(errors, GovernedLoopAdmissionValidationErrorCode.HashMismatch, "$.contentHash");
        }

        return Result(errors);
    }

    /// <summary>Validates one definitive immutable admission rejection.</summary>
    public static GovernedLoopAdmissionValidationResult Validate(GovernedLoopAdmissionRejection? rejection)
    {
        var errors = ValidateRejectionStructure(rejection);
        if (errors.Count == 0 && !GovernedLoopAdmissionContractHash.Matches(rejection))
        {
            Add(errors, GovernedLoopAdmissionValidationErrorCode.HashMismatch, "$.contentHash");
        }

        return Result(errors);
    }

    /// <summary>Validates one definitive admitted or rejected terminal outcome.</summary>
    public static GovernedLoopAdmissionValidationResult Validate(GovernedLoopAdmissionTerminalOutcome? outcome)
    {
        var errors = ValidateTerminalOutcomeStructure(outcome);
        if (errors.Count == 0 && !GovernedLoopAdmissionContractHash.Matches(outcome))
        {
            Add(errors, GovernedLoopAdmissionValidationErrorCode.HashMismatch, "$.contentHash");
        }

        return Result(errors);
    }

    internal static GovernedLoopAdmissionValidationResult ValidateForHash(GovernedLoopAdmissionIntent? intent)
        => Result(ValidateIntentStructure(intent));

    internal static GovernedLoopAdmissionValidationResult ValidateForHash(GovernedLoopAdmissionEvidence? evidence)
        => Result(ValidateEvidenceStructure(evidence));

    internal static GovernedLoopAdmissionValidationResult ValidateForHash(GovernedLoopAdmissionReceipt? receipt)
        => Result(ValidateReceiptStructure(receipt));

    internal static GovernedLoopAdmissionValidationResult ValidateForHash(GovernedLoopAdmissionRejection? rejection)
        => Result(ValidateRejectionStructure(rejection));

    internal static GovernedLoopAdmissionValidationResult ValidateForHash(GovernedLoopAdmissionTerminalOutcome? outcome)
        => Result(ValidateTerminalOutcomeStructure(outcome));

    private static List<GovernedLoopAdmissionValidationError> ValidateIntentStructure(GovernedLoopAdmissionIntent? intent)
    {
        var errors = new List<GovernedLoopAdmissionValidationError>();
        if (intent is null)
        {
            Add(errors, GovernedLoopAdmissionValidationErrorCode.Required, "$");
            return errors;
        }

        ValidateSchema(intent.SchemaVersion, "$.schemaVersion", errors);
        if (!ContextualRoleWorkspaceId.IsValid(intent.WorkspaceId))
        {
            Add(errors, GovernedLoopAdmissionValidationErrorCode.InvalidIdentity, "$.workspaceId");
        }

        ValidateToken(intent.OperationId, "$.operationId", GovernedLoopAdmissionLimits.MaxIdentifierCharacters, errors);
        ValidateHash(intent.RequestHash, "$.requestHash", errors);
        if (!GovernedLoopRevisionContractValidator.Validate(intent.Publication).IsValid)
        {
            Add(errors, GovernedLoopAdmissionValidationErrorCode.InvalidEvidence, "$.publication");
        }

        if (intent.AuthorityGrant?.GrantId is null || intent.AuthorityGrant.Revision is null || !AuthorityGrantHash.IsCanonical(intent.AuthorityGrant.ContentHash))
        {
            Add(errors, GovernedLoopAdmissionValidationErrorCode.InvalidEvidence, "$.authorityGrant");
        }

        if (intent.Role?.Identity is null
            || !ContextualRoleId.IsValid(intent.Role.Identity.RoleId)
            || intent.Role.Identity.Revision < 1
            || !GovernedLoopAdmissionContractHash.IsCanonicalHash(intent.Role.ContentHash))
        {
            Add(errors, GovernedLoopAdmissionValidationErrorCode.InvalidEvidence, "$.role");
        }

        if (intent.ActorId is null || !AuthorityActorId.TryParse(intent.ActorId.Value, out var parsedActor, out _) || !intent.ActorId.Equals(parsedActor))
        {
            Add(errors, GovernedLoopAdmissionValidationErrorCode.InvalidIdentity, "$.actorId");
        }

        ValidateToken(intent.Surface, "$.surface", GovernedLoopAdmissionLimits.MaxSurfaceCharacters, errors);
        ValidateHash(intent.GraphArtifactHash, "$.graphArtifactHash", errors);
        ValidateHash(intent.GraphLayoutHash, "$.graphLayoutHash", errors);
        return errors;
    }

    private static List<GovernedLoopAdmissionValidationError> ValidateEvidenceStructure(GovernedLoopAdmissionEvidence? evidence)
    {
        var errors = new List<GovernedLoopAdmissionValidationError>();
        if (evidence is null)
        {
            Add(errors, GovernedLoopAdmissionValidationErrorCode.Required, "$");
            return errors;
        }

        ValidateSchema(evidence.SchemaVersion, "$.schemaVersion", errors);
        ValidateHash(evidence.IntentHash, "$.intentHash", errors);
        if (!GovernedLoopExecutionValidator.Validate(evidence.Binding).IsValid || evidence.Binding?.ExecutionGeneration != 1)
        {
            Add(errors, GovernedLoopAdmissionValidationErrorCode.InvalidEvidence, "$.binding");
        }

        if (!AuthorityProfileValidator.ValidateCeiling(evidence.EffectiveAuthority).IsValid)
        {
            Add(errors, GovernedLoopAdmissionValidationErrorCode.InvalidEvidence, "$.effectiveAuthority");
        }

        if (!GovernedLoopAdmissionCapabilityGuard.IsValid(evidence.CapabilityAdmission))
        {
            Add(errors, GovernedLoopAdmissionValidationErrorCode.InvalidEvidence, "$.capabilityAdmission");
        }

        ValidateReferences(evidence.References, requireCompleteSet: true, "$.references", errors);
        ValidateUtc(evidence.EvaluatedAtUtc, "$.evaluatedAtUtc", errors);
        return errors;
    }

    private static List<GovernedLoopAdmissionValidationError> ValidateReceiptStructure(GovernedLoopAdmissionReceipt? receipt)
    {
        var errors = new List<GovernedLoopAdmissionValidationError>();
        if (receipt is null)
        {
            Add(errors, GovernedLoopAdmissionValidationErrorCode.Required, "$");
            return errors;
        }

        ValidateSchema(receipt.SchemaVersion, "$.schemaVersion", errors);
        AddNested(errors, ValidateIntentStructure(receipt.Intent), "$.intent");
        AddNested(errors, ValidateEvidenceStructure(receipt.Evidence), "$.evidence");
        ValidateUtc(receipt.RecordedAtUtc, "$.recordedAtUtc", errors);
        if (errors.Count == 0)
        {
            ValidateEvidenceBindings(receipt.Evidence, receipt.Intent, errors);
            if (!GovernedLoopAdmissionContractHash.Matches(receipt.Evidence))
            {
                Add(errors, GovernedLoopAdmissionValidationErrorCode.HashMismatch, "$.evidence.contentHash");
            }

            if (receipt.RecordedAtUtc < receipt.Evidence.EvaluatedAtUtc)
            {
                Add(errors, GovernedLoopAdmissionValidationErrorCode.InvalidTimestamp, "$.recordedAtUtc");
            }
        }

        return errors;
    }

    private static List<GovernedLoopAdmissionValidationError> ValidateRejectionStructure(GovernedLoopAdmissionRejection? rejection)
    {
        var errors = new List<GovernedLoopAdmissionValidationError>();
        if (rejection is null)
        {
            Add(errors, GovernedLoopAdmissionValidationErrorCode.Required, "$");
            return errors;
        }

        ValidateSchema(rejection.SchemaVersion, "$.schemaVersion", errors);
        AddNested(errors, ValidateIntentStructure(rejection.Intent), "$.intent");
        if (!Enum.IsDefined(rejection.FailureCode) || rejection.FailureCode == GovernedLoopAdmissionFailureCode.None)
        {
            Add(errors, GovernedLoopAdmissionValidationErrorCode.InvalidEnumeration, "$.failureCode");
        }

        if (Enum.IsDefined(rejection.FailureCode) && rejection.FailureCode != GovernedLoopAdmissionFailureCode.None)
        {
            ValidateReferences(rejection.References, RequiredRejectionEvidenceKinds(rejection.FailureCode), "$.references", errors);
        }
        else
        {
            ValidateReferences(rejection.References, requireCompleteSet: false, "$.references", errors);
        }
        ValidateUtc(rejection.RejectedAtUtc, "$.rejectedAtUtc", errors);
        return errors;
    }

    private static List<GovernedLoopAdmissionValidationError> ValidateTerminalOutcomeStructure(GovernedLoopAdmissionTerminalOutcome? outcome)
    {
        var errors = new List<GovernedLoopAdmissionValidationError>();
        if (outcome is null)
        {
            Add(errors, GovernedLoopAdmissionValidationErrorCode.Required, "$");
            return errors;
        }

        ValidateSchema(outcome.SchemaVersion, "$.schemaVersion", errors);
        AddNested(errors, ValidateIntentStructure(outcome.Intent), "$.intent");
        if (!Enum.IsDefined(outcome.Disposition) || outcome.Disposition == GovernedLoopAdmissionDisposition.Unknown)
        {
            Add(errors, GovernedLoopAdmissionValidationErrorCode.InvalidEnumeration, "$.disposition");
        }

        ValidateUtc(outcome.RecordedAtUtc, "$.recordedAtUtc", errors);
        var admitted = outcome.Disposition == GovernedLoopAdmissionDisposition.Admitted;
        var rejected = outcome.Disposition == GovernedLoopAdmissionDisposition.Rejected;
        if (admitted != (outcome.Receipt is not null) || rejected != (outcome.Rejection is not null))
        {
            Add(errors, GovernedLoopAdmissionValidationErrorCode.InvalidComposition, "$.disposition");
            return errors;
        }

        if (outcome.Receipt is not null)
        {
            AddNested(errors, ValidateReceiptStructure(outcome.Receipt), "$.receipt");
            if (errors.Count == 0 && (!GovernedLoopAdmissionContractHash.Matches(outcome.Receipt) || !SameIntent(outcome.Intent, outcome.Receipt.Intent) || outcome.RecordedAtUtc != outcome.Receipt.RecordedAtUtc))
            {
                Add(errors, GovernedLoopAdmissionValidationErrorCode.BindingMismatch, "$.receipt");
            }
        }

        if (outcome.Rejection is not null)
        {
            AddNested(errors, ValidateRejectionStructure(outcome.Rejection), "$.rejection");
            if (errors.Count == 0 && (!GovernedLoopAdmissionContractHash.Matches(outcome.Rejection) || !SameIntent(outcome.Intent, outcome.Rejection.Intent) || outcome.RecordedAtUtc != outcome.Rejection.RejectedAtUtc))
            {
                Add(errors, GovernedLoopAdmissionValidationErrorCode.BindingMismatch, "$.rejection");
            }
        }

        return errors;
    }

    private static void ValidateEvidenceBindings(GovernedLoopAdmissionEvidence evidence, GovernedLoopAdmissionIntent intent, List<GovernedLoopAdmissionValidationError> errors)
    {
        string intentHash;
        IReadOnlyList<GovernedLoopAdmissionEvidenceReference> expectedReferences;
        try
        {
            intentHash = GovernedLoopAdmissionContractHash.ComputeIntentHash(intent);
            expectedReferences = GovernedLoopAdmissionContractHash.CreateEvidenceReferences(intent, evidence.EffectiveAuthority, evidence.CapabilityAdmission);
        }
        catch (ArgumentException)
        {
            Add(errors, GovernedLoopAdmissionValidationErrorCode.InvalidEvidence, "$.evidence");
            return;
        }

        if (!string.Equals(evidence.IntentHash, intentHash, StringComparison.Ordinal)
            || evidence.Binding is null
            || !SameRevision(evidence.Binding.Revision, intent.Publication.Revision)
            || !string.Equals(evidence.CapabilityAdmission.WorkspaceScopeId, intent.WorkspaceId, StringComparison.Ordinal)
            || evidence.CapabilityAdmission.AdmittedAtUtc > evidence.EvaluatedAtUtc)
        {
            Add(errors, GovernedLoopAdmissionValidationErrorCode.BindingMismatch, "$.evidence");
        }

        if (!expectedReferences.SequenceEqual(evidence.References))
        {
            Add(errors, GovernedLoopAdmissionValidationErrorCode.EvidenceSetMismatch, "$.evidence.references");
        }
    }

    private static void ValidateReferences(IReadOnlyList<GovernedLoopAdmissionEvidenceReference>? references, bool requireCompleteSet, string path, List<GovernedLoopAdmissionValidationError> errors)
    {
        var expectedCount = Enum.GetValues<GovernedLoopAdmissionEvidenceKind>().Count(value => value != GovernedLoopAdmissionEvidenceKind.Unknown);
        if (references is null
            || references.Count == 0
            || references.Count > GovernedLoopAdmissionLimits.MaxEvidenceReferences
            || requireCompleteSet && references.Count != expectedCount)
        {
            Add(errors, GovernedLoopAdmissionValidationErrorCode.LimitExceeded, path);
            return;
        }

        var previousKind = GovernedLoopAdmissionEvidenceKind.Unknown;
        foreach (var reference in references)
        {
            if (reference is null
                || !Enum.IsDefined(reference.Kind)
                || reference.Kind == GovernedLoopAdmissionEvidenceKind.Unknown
                || reference.Kind <= previousKind
                || !GovernedLoopAdmissionContractHash.IsCanonicalHash(reference.EvidenceHash))
            {
                Add(errors, GovernedLoopAdmissionValidationErrorCode.EvidenceSetMismatch, path);
                return;
            }

            previousKind = reference.Kind;
        }
    }

    private static void ValidateReferences(IReadOnlyList<GovernedLoopAdmissionEvidenceReference>? references, IReadOnlyList<GovernedLoopAdmissionEvidenceKind> expectedKinds, string path, List<GovernedLoopAdmissionValidationError> errors)
    {
        ValidateReferences(references, requireCompleteSet: false, path, errors);
        if (references is null || errors.Any(error => string.Equals(error.Path, path, StringComparison.Ordinal)))
        {
            return;
        }

        if (!references.Select(reference => reference.Kind).SequenceEqual(expectedKinds))
        {
            Add(errors, GovernedLoopAdmissionValidationErrorCode.EvidenceSetMismatch, path);
        }
    }

    /// <summary>Gets the exact canonical evidence-kind set required for one definitive rejection classification.</summary>
    /// <param name="failureCode">The defined non-success rejection classification.</param>
    /// <returns>A defensively wrapped, canonically ordered evidence-kind set.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the code is undefined or represents success.</exception>
    public static IReadOnlyList<GovernedLoopAdmissionEvidenceKind> RequiredRejectionEvidenceKinds(GovernedLoopAdmissionFailureCode failureCode)
    {
        if (!Enum.IsDefined(failureCode) || failureCode == GovernedLoopAdmissionFailureCode.None)
        {
            throw new ArgumentOutOfRangeException(nameof(failureCode));
        }

        GovernedLoopAdmissionEvidenceKind[] kinds = failureCode switch
        {
            GovernedLoopAdmissionFailureCode.RoleMismatch =>
            [
                GovernedLoopAdmissionEvidenceKind.ContextualRoleRevision,
                GovernedLoopAdmissionEvidenceKind.AuthorityGrant,
                GovernedLoopAdmissionEvidenceKind.GraphArtifact
            ],
            GovernedLoopAdmissionFailureCode.RoleNotFound
                or GovernedLoopAdmissionFailureCode.RoleInactive
                or GovernedLoopAdmissionFailureCode.RoleReplaced
                or GovernedLoopAdmissionFailureCode.RoleWorkspaceMismatch
                or GovernedLoopAdmissionFailureCode.RoleSourceMismatch => [GovernedLoopAdmissionEvidenceKind.ContextualRoleRevision],
            GovernedLoopAdmissionFailureCode.GrantMismatch
                or GovernedLoopAdmissionFailureCode.GrantInactive => [GovernedLoopAdmissionEvidenceKind.AuthorityGrant],
            GovernedLoopAdmissionFailureCode.PublicationMismatch => [GovernedLoopAdmissionEvidenceKind.LoopPublication],
            GovernedLoopAdmissionFailureCode.GraphArtifactMismatch =>
            [
                GovernedLoopAdmissionEvidenceKind.GraphArtifact,
                GovernedLoopAdmissionEvidenceKind.GraphLayout
            ],
            GovernedLoopAdmissionFailureCode.AuthorityDenied =>
            [
                GovernedLoopAdmissionEvidenceKind.ContextualRoleRevision,
                GovernedLoopAdmissionEvidenceKind.AuthorityGrant,
                GovernedLoopAdmissionEvidenceKind.LoopPublication,
                GovernedLoopAdmissionEvidenceKind.GraphArtifact,
                GovernedLoopAdmissionEvidenceKind.EffectiveAuthority
            ],
            GovernedLoopAdmissionFailureCode.CapabilityResolutionDenied =>
            [
                GovernedLoopAdmissionEvidenceKind.EffectiveAuthority,
                GovernedLoopAdmissionEvidenceKind.CapabilityAdmission
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(failureCode))
        };

        return Array.AsReadOnly(kinds);
    }

    private static bool SameIntent(GovernedLoopAdmissionIntent first, GovernedLoopAdmissionIntent second)
    {
        try
        {
            return string.Equals(GovernedLoopAdmissionContractHash.ComputeIntentHash(first), GovernedLoopAdmissionContractHash.ComputeIntentHash(second), StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool SameRevision(GovernedLoopRevisionReference? first, GovernedLoopRevisionReference? second)
        => first is not null
            && second is not null
            && first.SchemaVersion == second.SchemaVersion
            && string.Equals(first.GraphId, second.GraphId, StringComparison.Ordinal)
            && string.Equals(first.RevisionId, second.RevisionId, StringComparison.Ordinal)
            && string.Equals(first.ExecutableHash, second.ExecutableHash, StringComparison.Ordinal);

    private static void ValidateSchema(int schemaVersion, string path, List<GovernedLoopAdmissionValidationError> errors)
    {
        if (schemaVersion != GovernedLoopAdmissionLimits.CurrentSchemaVersion)
        {
            Add(errors, GovernedLoopAdmissionValidationErrorCode.UnsupportedSchemaVersion, path);
        }
    }

    private static void ValidateToken(string? value, string path, int maximumLength, List<GovernedLoopAdmissionValidationError> errors)
    {
        if (string.IsNullOrEmpty(value)
            || value.Length > maximumLength
            || value[0] is not (>= 'a' and <= 'z') and not (>= '0' and <= '9')
            || value[^1] is not (>= 'a' and <= 'z') and not (>= '0' and <= '9')
            || value.Any(character => character is not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not '-' and not '_' and not '.'))
        {
            Add(errors, GovernedLoopAdmissionValidationErrorCode.InvalidIdentity, path);
        }
    }

    private static void ValidateHash(string? value, string path, List<GovernedLoopAdmissionValidationError> errors)
    {
        if (!GovernedLoopAdmissionContractHash.IsCanonicalHash(value))
        {
            Add(errors, GovernedLoopAdmissionValidationErrorCode.InvalidHash, path);
        }
    }

    private static void ValidateUtc(DateTimeOffset value, string path, List<GovernedLoopAdmissionValidationError> errors)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            Add(errors, GovernedLoopAdmissionValidationErrorCode.InvalidTimestamp, path);
        }
    }

    private static GovernedLoopAdmissionValidationResult Result(IEnumerable<GovernedLoopAdmissionValidationError> errors)
        => GovernedLoopAdmissionValidationResult.FromErrors(errors);

    private static void AddNested(List<GovernedLoopAdmissionValidationError> target, IEnumerable<GovernedLoopAdmissionValidationError> source, string prefix)
    {
        foreach (var error in source)
        {
            Add(target, error.Code, error.Path == "$" ? prefix : prefix + error.Path[1..]);
        }
    }

    private static void Add(List<GovernedLoopAdmissionValidationError> errors, GovernedLoopAdmissionValidationErrorCode code, string path)
    {
        if (errors.Count >= GovernedLoopAdmissionLimits.MaxValidationErrors)
        {
            return;
        }

        errors.Add(new GovernedLoopAdmissionValidationError(code, path.Length <= GovernedLoopAdmissionLimits.MaxErrorPathCharacters ? path : path[..GovernedLoopAdmissionLimits.MaxErrorPathCharacters]));
    }
}
