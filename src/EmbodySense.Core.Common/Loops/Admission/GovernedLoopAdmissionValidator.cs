using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Admission.Models;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Inference.Profiles;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions;

namespace EmbodySense.Core.Common.Loops.Admission;

/// <summary>Validates bounded schema-1 governed-loop admission intent, evidence, and terminal outcomes.</summary>
public static class GovernedLoopAdmissionValidator
{
    /// <summary>Validates one bounded structured model-routing denial proof.</summary>
    public static GovernedLoopAdmissionValidationResult Validate(GovernedLoopAdmissionModelRoutingDenialProof? proof)
        => Result(ValidateModelRoutingDenialProofStructure(proof));

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

    internal static GovernedLoopAdmissionValidationResult ValidateRejectionProofsForHash(
        GovernedLoopAdmissionFailureCode failureCode,
        GovernedLoopAdmissionAuthorityDenialProof? authorityDenial,
        GovernedLoopAdmissionCapabilityDenialProof? capabilityDenial,
        GovernedLoopAdmissionModelRoutingDenialProof? modelRoutingDenial = null)
        => Result(ValidateRejectionProofs(failureCode, authorityDenial, capabilityDenial, modelRoutingDenial));

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

        if (evidence.GrantProfile?.Reference?.ProfileId is null
            || evidence.GrantProfile.Reference.Revision is null
            || evidence.GrantProfile.ContentHash is null
            || !AuthorityGrantHash.IsCanonical(evidence.GrantProfile.ContentHash.Value))
        {
            Add(errors, GovernedLoopAdmissionValidationErrorCode.InvalidEvidence, "$.grantProfile");
        }

        if (evidence.GrantBoundary is null
            || evidence.GrantBoundary.EffectiveAtUtc == default
            || evidence.GrantBoundary.EffectiveAtUtc.Offset != TimeSpan.Zero
            || evidence.GrantBoundary.ExpiresAtUtc is { } expiry && (expiry.Offset != TimeSpan.Zero || expiry <= evidence.GrantBoundary.EffectiveAtUtc)
            || !Enum.IsDefined(evidence.GrantBoundary.CompletionConstraint)
            || evidence.GrantBoundary.CompletionConstraint == AuthorityGrantCompletionConstraintKind.Unknown)
        {
            Add(errors, GovernedLoopAdmissionValidationErrorCode.InvalidEvidence, "$.grantBoundary");
        }

        ValidateHash(evidence.GrantDependencyEvidenceHash, "$.grantDependencyEvidenceHash", errors);

        if (!AuthorityProfileValidator.ValidateCeiling(evidence.EffectiveAuthority).IsValid)
        {
            Add(errors, GovernedLoopAdmissionValidationErrorCode.InvalidEvidence, "$.effectiveAuthority");
        }

        if (!GovernedLoopAdmissionCapabilityGuard.IsValid(evidence.CapabilityAdmission))
        {
            Add(errors, GovernedLoopAdmissionValidationErrorCode.InvalidEvidence, "$.capabilityAdmission");
        }

        if (!GovernedModelContractValidator.IsValid(evidence.ModelRoutingAdmission))
        {
            Add(errors, GovernedLoopAdmissionValidationErrorCode.InvalidEvidence, "$.modelRoutingAdmission");
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
            AddNested(errors, ValidateRejectionProofs(rejection.FailureCode, rejection.AuthorityDenial, rejection.CapabilityDenial, rejection.ModelRoutingDenial), "$.proofs");
        }
        else
        {
            ValidateReferences(rejection.References, requireCompleteSet: false, "$.references", errors);
        }
        ValidateUtc(rejection.RejectedAtUtc, "$.rejectedAtUtc", errors);
        if (rejection.AuthorityDenial?.BoundaryReceipt is { } authorityReceipt
            && authorityReceipt.EvaluatedAtUtc != rejection.RejectedAtUtc)
        {
            Add(errors, GovernedLoopAdmissionValidationErrorCode.InvalidTimestamp, "$.authorityDenial.boundaryReceipt.evaluatedAtUtc");
        }

        if (rejection.CapabilityDenial is { } capabilityDenial
            && capabilityDenial.EvaluatedAtUtc != rejection.RejectedAtUtc)
        {
            Add(errors, GovernedLoopAdmissionValidationErrorCode.InvalidTimestamp, "$.capabilityDenial.evaluatedAtUtc");
        }

        if (rejection.ModelRoutingDenial is { } modelRoutingDenial
            && modelRoutingDenial.EvaluatedAtUtc != rejection.RejectedAtUtc)
        {
            Add(errors, GovernedLoopAdmissionValidationErrorCode.InvalidTimestamp, "$.modelRoutingDenial.evaluatedAtUtc");
        }

        if (errors.Count == 0)
        {
            IReadOnlyList<GovernedLoopAdmissionEvidenceReference> expectedReferences;
            try
            {
                expectedReferences = GovernedLoopAdmissionContractHash.CreateRejectionEvidenceReferences(
                    rejection.Intent,
                    rejection.FailureCode,
                    rejection.AuthorityDenial,
                    rejection.CapabilityDenial,
                    rejection.ModelRoutingDenial);
            }
            catch (ArgumentException)
            {
                Add(errors, GovernedLoopAdmissionValidationErrorCode.InvalidEvidence, "$.proofs");
                return errors;
            }

            if (!expectedReferences.SequenceEqual(rejection.References))
            {
                Add(errors, GovernedLoopAdmissionValidationErrorCode.EvidenceSetMismatch, "$.references");
            }
        }

        return errors;
    }

    private static List<GovernedLoopAdmissionValidationError> ValidateRejectionProofs(
        GovernedLoopAdmissionFailureCode failureCode,
        GovernedLoopAdmissionAuthorityDenialProof? authorityDenial,
        GovernedLoopAdmissionCapabilityDenialProof? capabilityDenial,
        GovernedLoopAdmissionModelRoutingDenialProof? modelRoutingDenial)
    {
        var errors = new List<GovernedLoopAdmissionValidationError>();
        var requiresAuthority = failureCode == GovernedLoopAdmissionFailureCode.AuthorityDenied;
        var requiresCapability = failureCode == GovernedLoopAdmissionFailureCode.CapabilityResolutionDenied;
        var requiresModelRouting = failureCode == GovernedLoopAdmissionFailureCode.ModelRoutingDenied;
        if (requiresAuthority != (authorityDenial is not null)
            || requiresCapability != (capabilityDenial is not null)
            || requiresModelRouting != (modelRoutingDenial is not null))
        {
            Add(errors, GovernedLoopAdmissionValidationErrorCode.InvalidComposition, "$");
        }

        if (authorityDenial is not null)
        {
            AddNested(errors, ValidateAuthorityDenialProofStructure(authorityDenial), "$.authorityDenial");
        }

        if (capabilityDenial is not null)
        {
            AddNested(errors, ValidateCapabilityDenialProofStructure(capabilityDenial), "$.capabilityDenial");
        }


        if (modelRoutingDenial is not null)
        {
            AddNested(errors, ValidateModelRoutingDenialProofStructure(modelRoutingDenial), "$.modelRoutingDenial");
        }

        return errors;
    }

    private static List<GovernedLoopAdmissionValidationError> ValidateModelRoutingDenialProofStructure(GovernedLoopAdmissionModelRoutingDenialProof? proof)
    {
        var errors = new List<GovernedLoopAdmissionValidationError>();
        if (proof is null)
        {
            Add(errors, GovernedLoopAdmissionValidationErrorCode.Required, "$");
            return errors;
        }

        ValidateSchema(proof.SchemaVersion, "$.schemaVersion", errors);
        if (!CustomLoopArtifactIdentifier.IsValid(proof.NodeId)
            || !CustomLoopArtifactIdentifier.IsValid(proof.NodeTypeId))
        {
            Add(errors, GovernedLoopAdmissionValidationErrorCode.InvalidIdentity, "$.node");
        }
        ValidateHash(proof.PolicyHash, "$.policyHash", errors);
        ValidateHash(proof.EffectiveAuthorityReferenceHash, "$.effectiveAuthorityReferenceHash", errors);
        ValidateHash(proof.CapabilityAdmissionReferenceHash, "$.capabilityAdmissionReferenceHash", errors);
        ValidateUtc(proof.EvaluatedAtUtc, "$.evaluatedAtUtc", errors);
        if (!Enum.IsDefined(proof.Reason))
        {
            Add(errors, GovernedLoopAdmissionValidationErrorCode.InvalidEnumeration, "$.reason");
        }
        if (proof.CandidateProfileId is not null
            && (!CapabilityId.TryParse(proof.CandidateProfileId.Value, out var parsed, out _) || !proof.CandidateProfileId.Equals(parsed)))
        {
            Add(errors, GovernedLoopAdmissionValidationErrorCode.InvalidIdentity, "$.candidateProfileId");
        }
        if (proof.Reason == GovernedLoopAdmissionModelRoutingDenialReason.DefaultNotConfigured && proof.CandidateProfileId is not null
            || proof.Reason != GovernedLoopAdmissionModelRoutingDenialReason.DefaultNotConfigured && proof.CandidateProfileId is null)
        {
            Add(errors, GovernedLoopAdmissionValidationErrorCode.InvalidComposition, "$.candidateProfileId");
        }

        return errors;
    }

    private static List<GovernedLoopAdmissionValidationError> ValidateAuthorityDenialProofStructure(GovernedLoopAdmissionAuthorityDenialProof? proof)
    {
        var errors = new List<GovernedLoopAdmissionValidationError>();
        if (proof is null)
        {
            Add(errors, GovernedLoopAdmissionValidationErrorCode.Required, "$");
            return errors;
        }

        ValidateSchema(proof.SchemaVersion, "$.schemaVersion", errors);
        if (!AuthorityProfileValidator.ValidateCeiling(proof.CandidateCeiling).IsValid)
        {
            Add(errors, GovernedLoopAdmissionValidationErrorCode.InvalidEvidence, "$.candidateCeiling");
        }

        if (!AuthorityProfileValidator.ValidateCeiling(proof.EffectiveCeiling).IsValid || !IsCanonicalEmptyCeiling(proof.EffectiveCeiling))
        {
            Add(errors, GovernedLoopAdmissionValidationErrorCode.InvalidEvidence, "$.effectiveCeiling");
        }

        if (!AuthorityBoundaryReceiptFactory.Validate(proof.BoundaryReceipt).IsValid
            || proof.BoundaryReceipt?.Decision != AuthorityBoundaryDecision.Deny)
        {
            Add(errors, GovernedLoopAdmissionValidationErrorCode.InvalidEvidence, "$.boundaryReceipt");
        }
        else
        {
            ValidateUtc(proof.BoundaryReceipt.EvaluatedAtUtc, "$.boundaryReceipt.evaluatedAtUtc", errors);
            if (!proof.BoundaryReceipt.Conditions.SequenceEqual(proof.BoundaryReceipt.Conditions.OrderBy(item => item.Decision).ThenBy(item => item.Reason))
                || !proof.BoundaryReceipt.Profiles.SequenceEqual(proof.BoundaryReceipt.Profiles.OrderBy(item => item.ProfileId).ThenBy(item => item.Revision)))
            {
                Add(errors, GovernedLoopAdmissionValidationErrorCode.InvalidEvidence, "$.boundaryReceipt");
            }
        }

        return errors;
    }

    private static List<GovernedLoopAdmissionValidationError> ValidateCapabilityDenialProofStructure(GovernedLoopAdmissionCapabilityDenialProof? proof)
    {
        var errors = new List<GovernedLoopAdmissionValidationError>();
        if (proof is null)
        {
            Add(errors, GovernedLoopAdmissionValidationErrorCode.Required, "$");
            return errors;
        }

        ValidateSchema(proof.SchemaVersion, "$.schemaVersion", errors);
        if (!CapabilityDependencyManifestHash.TryCompute(proof.Requirements, out var requirementsHash, out _)
            || !string.Equals(proof.RequirementsHash, requirementsHash?.Value, StringComparison.Ordinal))
        {
            Add(errors, GovernedLoopAdmissionValidationErrorCode.InvalidEvidence, "$.requirements");
        }

        if (!AuthorityProfileValidator.ValidateCeiling(proof.EffectiveAuthority).IsValid)
        {
            Add(errors, GovernedLoopAdmissionValidationErrorCode.InvalidEvidence, "$.effectiveAuthority");
        }

        ValidateUtc(proof.EvaluatedAtUtc, "$.evaluatedAtUtc", errors);
        if (proof.Violations is null
            || proof.Violations.Count is < 1 or > GovernedLoopAdmissionLimits.MaxCapabilityDenialViolations
            || proof.Violations.Any(item => item?.DependencyId is null
                || item.CompatibleVersionRange is null
                || item.Reason != GovernedLoopAdmissionCapabilityDenialReason.RequiredCapabilityOutsideEffectiveAuthority))
        {
            Add(errors, GovernedLoopAdmissionValidationErrorCode.InvalidEvidence, "$.violations");
        }

        if (errors.Count == 0)
        {
            var expected = ComputeCapabilityDenialViolations(proof.Requirements, proof.EffectiveAuthority);
            if (expected.Count == 0 || !expected.SequenceEqual(proof.Violations!))
            {
                Add(errors, GovernedLoopAdmissionValidationErrorCode.BindingMismatch, "$.violations");
            }
        }

        return errors;
    }

    private static IReadOnlyList<GovernedLoopAdmissionCapabilityDenialViolation> ComputeCapabilityDenialViolations(
        CapabilityDependencyManifest requirements,
        AuthorityCeiling effectiveAuthority)
        => Array.AsReadOnly(requirements.Required
            .Where(dependency => !effectiveAuthority.Capabilities.Any(identity => identity.Id.Equals(dependency.CapabilityId) && dependency.CompatibleVersionRange.Contains(identity.Version)))
            .OrderBy(dependency => dependency.CapabilityId.Value, StringComparer.Ordinal)
            .ThenBy(dependency => dependency.CompatibleVersionRange.Value, StringComparer.Ordinal)
            .Select(dependency => new GovernedLoopAdmissionCapabilityDenialViolation(
                dependency.CapabilityId,
                dependency.CompatibleVersionRange,
                GovernedLoopAdmissionCapabilityDenialReason.RequiredCapabilityOutsideEffectiveAuthority))
            .ToArray());

    private static bool IsCanonicalEmptyCeiling(AuthorityCeiling? ceiling)
        => ceiling is
        {
            Capabilities.Count: 0,
            DataClasses.Count: 0,
            MaxTargetCount: 0,
            MaxSideEffectClass: CapabilitySideEffectClass.None,
            AllowsRecurrence: false,
            AllowsExternalPublication: false,
            AllowsIrreversibleAction: false
        };

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
            expectedReferences = GovernedLoopAdmissionContractHash.CreateEvidenceReferences(intent, evidence.EffectiveAuthority, evidence.CapabilityAdmission, evidence.ModelRoutingAdmission);
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
            || !string.Equals(evidence.ModelRoutingAdmission.WorkspaceId, intent.WorkspaceId, StringComparison.Ordinal)
            || !string.Equals(evidence.ModelRoutingAdmission.AdmissionOperationId, intent.OperationId, StringComparison.Ordinal)
            || !string.Equals(evidence.ModelRoutingAdmission.AdmissionIntentHash, intentHash, StringComparison.Ordinal)
            || !string.Equals(evidence.ModelRoutingAdmission.ExecutionBindingReferenceHash, GovernedLoopAdmissionContractHash.ComputeExecutionBindingReferenceHash(evidence.Binding), StringComparison.Ordinal)
            || !string.Equals(evidence.ModelRoutingAdmission.RunId, evidence.Binding.RunId, StringComparison.Ordinal)
            || !string.Equals(evidence.ModelRoutingAdmission.GraphId, evidence.Binding.Revision.GraphId, StringComparison.Ordinal)
            || !string.Equals(evidence.ModelRoutingAdmission.GraphRevisionId, evidence.Binding.Revision.RevisionId, StringComparison.Ordinal)
            || !string.Equals(evidence.ModelRoutingAdmission.GraphExecutableHash, evidence.Binding.Revision.ExecutableHash, StringComparison.Ordinal)
            || evidence.ModelRoutingAdmission.ExecutionGeneration != evidence.Binding.ExecutionGeneration
            || !string.Equals(evidence.ModelRoutingAdmission.OwningRoleId, intent.Role.Identity.RoleId, StringComparison.Ordinal)
            || evidence.ModelRoutingAdmission.OwningRoleRevision != intent.Role.Identity.Revision
            || !string.Equals(evidence.ModelRoutingAdmission.OwningRoleContentHash, intent.Role.ContentHash, StringComparison.Ordinal)
            || !string.Equals(evidence.ModelRoutingAdmission.CapabilityAdmissionReferenceHash, GovernedLoopAdmissionContractHash.ComputeCapabilityAdmissionReferenceHash(evidence.CapabilityAdmission), StringComparison.Ordinal)
            || !string.Equals(evidence.ModelRoutingAdmission.AuthorityAdmissionReferenceHash, GovernedLoopAdmissionContractHash.ComputeAdmissionAuthorityReferenceHash(evidence.GrantProfile, evidence.GrantBoundary, evidence.GrantDependencyEvidenceHash, evidence.EffectiveAuthority), StringComparison.Ordinal)
            || evidence.ModelRoutingAdmission.EvaluatedAtUtc != evidence.EvaluatedAtUtc
            || evidence.CapabilityAdmission.AdmittedAtUtc > evidence.EvaluatedAtUtc
            || evidence.GrantBoundary.EffectiveAtUtc > evidence.EvaluatedAtUtc
            || evidence.GrantBoundary.ExpiresAtUtc is { } expiry && expiry <= evidence.EvaluatedAtUtc)
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
                GovernedLoopAdmissionEvidenceKind.GraphArtifact,
                GovernedLoopAdmissionEvidenceKind.EffectiveAuthority,
                GovernedLoopAdmissionEvidenceKind.CapabilityAdmission
            ],
            GovernedLoopAdmissionFailureCode.ModelRoutingDenied =>
            [
                GovernedLoopAdmissionEvidenceKind.GraphArtifact,
                GovernedLoopAdmissionEvidenceKind.EffectiveAuthority,
                GovernedLoopAdmissionEvidenceKind.CapabilityAdmission,
                GovernedLoopAdmissionEvidenceKind.ModelRoutingAdmission
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
