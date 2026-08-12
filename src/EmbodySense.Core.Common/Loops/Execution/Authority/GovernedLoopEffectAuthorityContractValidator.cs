using System.Text;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Execution.Authority.Models;
using EmbodySense.Core.Common.Loops.Revisions;

namespace EmbodySense.Core.Common.Loops.Execution.Authority;

/// <summary>Validates bounded schema-version-1 effect-authority proofs and decisions without granting authority.</summary>
public static class GovernedLoopEffectAuthorityContractValidator
{
    /// <summary>Validates one exact admitted or current authority proof.</summary>
    /// <param name="proof">The proof to validate.</param>
    /// <returns>A bounded value-free validation result.</returns>
    public static GovernedLoopEffectAuthorityValidationResult Validate(GovernedLoopEffectAuthorityProof? proof)
    {
        var errors = new List<GovernedLoopEffectAuthorityValidationError>();
        ValidateProof(proof, "$", errors);
        return Result(errors);
    }

    /// <summary>Validates one complete immutable effect-authority decision, including its canonical content hash.</summary>
    /// <param name="decision">The decision to validate.</param>
    /// <returns>A bounded value-free validation result.</returns>
    public static GovernedLoopEffectAuthorityValidationResult Validate(GovernedLoopEffectAuthorityDecision? decision)
    {
        var errors = ValidateStructure(decision, validateContentHash: true);
        if (errors.Count == 0 && !GovernedLoopEffectAuthorityContractHash.Matches(decision))
        {
            Add(errors, GovernedLoopEffectAuthorityValidationErrorCode.HashMismatch, "$.contentHash");
        }

        return Result(errors);
    }

    internal static GovernedLoopEffectAuthorityValidationResult ValidateForHash(GovernedLoopEffectAuthorityDecision? decision)
        => Result(ValidateStructure(decision, validateContentHash: false));

    private static List<GovernedLoopEffectAuthorityValidationError> ValidateStructure(GovernedLoopEffectAuthorityDecision? decision, bool validateContentHash)
    {
        var errors = new List<GovernedLoopEffectAuthorityValidationError>();
        if (decision is null)
        {
            Add(errors, GovernedLoopEffectAuthorityValidationErrorCode.Required, "$");
            return errors;
        }

        ValidateSchema(decision.SchemaVersion, "$.schemaVersion", errors);
        ValidateIdentifier(decision.RunId, "$.runId", errors);
        ValidatePositive(decision.ExecutionGeneration, GovernedLoopEffectAuthorityContractLimits.MaxExecutionGeneration, "$.executionGeneration", errors);
        ValidateIdentifier(decision.NodeId, "$.nodeId", errors);
        ValidatePositive(decision.NodeAttempt, GovernedLoopEffectAuthorityContractLimits.MaxNodeAttempt, "$.nodeAttempt", errors);
        ValidateIdentifier(decision.EffectOperationId, "$.effectOperationId", errors);
        ValidateIdentifier(decision.CorrelationId, "$.correlationId", errors);
        ValidateEnumeration(decision.BoundaryKind, "$.boundaryKind", errors);
        ValidateHash(decision.AdmissionReceiptHash, "$.admissionReceiptHash", errors);
        ValidateProof(decision.AdmittedAuthority, "$.admittedAuthority", errors);
        if (decision.CurrentAuthority is not null)
        {
            ValidateProof(decision.CurrentAuthority, "$.currentAuthority", errors);
        }

        ValidateCeiling(decision.RequiredAuthority, "$.requiredAuthority", errors);
        ValidateCeiling(decision.EffectiveAuthority, "$.effectiveAuthority", errors);
        ValidatePins(decision.RequiredCapabilityPins, GovernedLoopEffectAuthorityContractLimits.MaxRequiredCapabilityPins, requireNonEmpty: true, "$.requiredCapabilityPins", errors);
        ValidateEnumeration(decision.Disposition, "$.disposition", errors);
        ValidateEnumeration(decision.Reason, "$.reason", errors);
        ValidateUtc(decision.EvaluatedAtUtc, "$.evaluatedAtUtc", errors);
        if (validateContentHash)
        {
            ValidateHash(decision.ContentHash, "$.contentHash", errors);
        }

        if (errors.Count == 0)
        {
            ValidateComposition(decision, errors);
        }

        return errors;
    }

    private static void ValidateProof(GovernedLoopEffectAuthorityProof? proof, string path, List<GovernedLoopEffectAuthorityValidationError> errors)
    {
        if (proof is null)
        {
            Add(errors, GovernedLoopEffectAuthorityValidationErrorCode.Required, path);
            return;
        }

        ValidateSchema(proof.SchemaVersion, $"{path}.schemaVersion", errors);
        ValidateGrant(proof.Grant, $"{path}.grant", errors);
        ValidateBinding(proof.Binding, $"{path}.binding", errors);
        ValidateEnumeration(proof.GrantStatus, $"{path}.grantStatus", errors);
        ValidateEnumeration(proof.GrantPosture, $"{path}.grantPosture", errors);
        ValidateBoundary(proof.Boundary, $"{path}.boundary", errors);
        ValidateCeiling(proof.Ceiling, $"{path}.ceiling", errors);
        ValidatePins(proof.CapabilityPins, GovernedLoopEffectAuthorityContractLimits.MaxCapabilityPins, requireNonEmpty: false, $"{path}.capabilityPins", errors);
        ValidatePins(proof.ObservedCapabilityPins, GovernedLoopEffectAuthorityContractLimits.MaxCapabilityPins, requireNonEmpty: false, $"{path}.observedCapabilityPins", errors);
        if (proof.DependencyEvidenceHash is not null)
        {
            ValidateHash(proof.DependencyEvidenceHash, $"{path}.dependencyEvidenceHash", errors);
        }

        ValidateProofPosture(proof, path, errors);
        if (AuthorityProfileValidator.ValidateCeiling(proof.Ceiling).IsValid
            && IsPinSetValid(proof.CapabilityPins, GovernedLoopEffectAuthorityContractLimits.MaxCapabilityPins, requireNonEmpty: false)
            && proof.CapabilityPins.Any(pin => !proof.Ceiling.Capabilities.Contains(pin.DescriptorIdentity)))
        {
            Add(errors, GovernedLoopEffectAuthorityValidationErrorCode.CapabilityMismatch, $"{path}.capabilityPins");
        }

        if (IsPinSetValid(proof.CapabilityPins, GovernedLoopEffectAuthorityContractLimits.MaxCapabilityPins, requireNonEmpty: false)
            && IsPinSetValid(proof.ObservedCapabilityPins, GovernedLoopEffectAuthorityContractLimits.MaxCapabilityPins, requireNonEmpty: false)
            && proof.ObservedCapabilityPins.Any(observed => proof.CapabilityPins.Any(active => active.DescriptorIdentity.Id.Equals(observed.DescriptorIdentity.Id))))
        {
            Add(errors, GovernedLoopEffectAuthorityValidationErrorCode.CapabilityMismatch, $"{path}.observedCapabilityPins");
        }
    }

    private static void ValidateProofPosture(GovernedLoopEffectAuthorityProof proof, string path, List<GovernedLoopEffectAuthorityValidationError> errors)
    {
        var lifecycleMatches = proof.GrantPosture switch
        {
            GovernedLoopEffectAuthorityGrantPosture.Active
                or GovernedLoopEffectAuthorityGrantPosture.NotEffective
                or GovernedLoopEffectAuthorityGrantPosture.Stale
                or GovernedLoopEffectAuthorityGrantPosture.ProfileUnavailable
                or GovernedLoopEffectAuthorityGrantPosture.RoleUnavailable
                or GovernedLoopEffectAuthorityGrantPosture.LoopUnavailable
                or GovernedLoopEffectAuthorityGrantPosture.CeilingExceeded
                or GovernedLoopEffectAuthorityGrantPosture.Completed => proof.GrantStatus == AuthorityGrantLifecycleStatus.Active,
            GovernedLoopEffectAuthorityGrantPosture.Suspended => proof.GrantStatus == AuthorityGrantLifecycleStatus.Suspended,
            GovernedLoopEffectAuthorityGrantPosture.Revoked => proof.GrantStatus == AuthorityGrantLifecycleStatus.Revoked,
            GovernedLoopEffectAuthorityGrantPosture.Expired => proof.GrantStatus is AuthorityGrantLifecycleStatus.Active or AuthorityGrantLifecycleStatus.Expired,
            _ => false
        };
        if (!lifecycleMatches)
        {
            Add(errors, GovernedLoopEffectAuthorityValidationErrorCode.InvalidProof, $"{path}.grantPosture");
        }
    }

    private static void ValidateGrant(AuthorityGrantReference? grant, string path, List<GovernedLoopEffectAuthorityValidationError> errors)
    {
        if (grant?.GrantId is null || grant.Revision is null)
        {
            Add(errors, GovernedLoopEffectAuthorityValidationErrorCode.InvalidProof, path);
            return;
        }

        if (!IsPrefixedSha256(grant.ContentHash))
        {
            Add(errors, GovernedLoopEffectAuthorityValidationErrorCode.InvalidHash, $"{path}.contentHash");
        }
    }

    private static void ValidateBinding(AuthorityGrantBinding? binding, string path, List<GovernedLoopEffectAuthorityValidationError> errors)
    {
        if (binding?.Profile?.Reference?.ProfileId is null
            || binding.Profile.Reference.Revision is null
            || binding.Profile.ContentHash is null
            || binding.Role?.Identity is null
            || !ContextualRoleId.IsValid(binding.Role.Identity.RoleId)
            || binding.Role.Identity.Revision < 1
            || !IsHash(binding.Role.ContentHash)
            || !GovernedLoopRevisionContractValidator.Validate(binding.Loop).IsValid)
        {
            Add(errors, GovernedLoopEffectAuthorityValidationErrorCode.InvalidProof, path);
        }
    }

    private static void ValidateBoundary(AuthorityGrantBoundary? boundary, string path, List<GovernedLoopEffectAuthorityValidationError> errors)
    {
        if (boundary is null
            || !IsUtc(boundary.EffectiveAtUtc)
            || boundary.ExpiresAtUtc is { } expiry && (!IsUtc(expiry) || expiry <= boundary.EffectiveAtUtc)
            || !IsSupported(boundary.CompletionConstraint))
        {
            Add(errors, GovernedLoopEffectAuthorityValidationErrorCode.InvalidProof, path);
        }
    }

    private static void ValidateCeiling(AuthorityCeiling? ceiling, string path, List<GovernedLoopEffectAuthorityValidationError> errors)
    {
        if (!AuthorityProfileValidator.ValidateCeiling(ceiling).IsValid)
        {
            Add(errors, GovernedLoopEffectAuthorityValidationErrorCode.InvalidProof, path);
        }
    }

    private static void ValidatePins(
        IReadOnlyList<CapabilityAdmissionPin>? pins,
        int maximum,
        bool requireNonEmpty,
        string path,
        List<GovernedLoopEffectAuthorityValidationError> errors)
    {
        if (pins is null)
        {
            Add(errors, GovernedLoopEffectAuthorityValidationErrorCode.Required, path);
            return;
        }

        if (pins.Count > maximum)
        {
            Add(errors, GovernedLoopEffectAuthorityValidationErrorCode.LimitExceeded, path);
            return;
        }

        if (!IsPinSetValid(pins, maximum, requireNonEmpty))
        {
            Add(errors, GovernedLoopEffectAuthorityValidationErrorCode.CapabilityMismatch, path);
        }
    }

    private static bool IsPinSetValid(IReadOnlyList<CapabilityAdmissionPin>? pins, int maximum, bool requireNonEmpty)
    {
        if (pins is null || pins.Count > maximum || requireNonEmpty && pins.Count == 0)
        {
            return false;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pin in pins)
        {
            if (!IsPinValid(pin) || !ids.Add(pin.DescriptorIdentity.Id.Value))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsPinValid(CapabilityAdmissionPin? pin)
    {
        return pin?.DescriptorIdentity?.Id is not null
            && pin.DescriptorIdentity.Version is not null
            && pin.DescriptorIdentity.Hash is not null
            && IsSupported(pin.Kind)
            && pin.Implementation?.ProviderId is not null
            && IsCanonicalPath(pin.Implementation.ImplementationId, CapabilityContractLimits.MaxImplementationIdCharacters)
            && IsProvenanceValid(pin.Provenance)
            && IsArtifactValid(pin.Artifact)
            && IsSafeNormalized(pin.SafeDescription, CapabilityContractLimits.MaxPurposeCharacters);
    }

    private static bool IsProvenanceValid(CapabilityProvenance? provenance)
    {
        if (provenance is null || !IsSupported(provenance.Kind) || !IsSafeSourceUri(provenance.SourceUri))
        {
            return false;
        }

        if (provenance.SourceRevision is not null
            && (provenance.SourceRevision.Length is < 1 or > CapabilityContractLimits.MaxSourceRevisionCharacters
                || provenance.SourceRevision.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-' or '/' or '@'))))
        {
            return false;
        }

        return provenance.Kind != CapabilityProvenanceKind.RemoteArtifact || provenance.Integrity is not null;
    }

    private static bool IsArtifactValid(CapabilityDependencyArtifactMetadata? artifact)
        => artifact is not null
            && (artifact.Signature is null
                || artifact.Signature.Length is > 0 and <= CapabilityContractLimits.MaxArtifactSignatureCharacters
                && artifact.Signature.All(character => character is >= (char)0x21 and <= (char)0x7e));

    private static bool IsSafeSourceUri(string? value)
    {
        if (string.IsNullOrEmpty(value)
            || value.Length > CapabilityContractLimits.MaxSourceUriCharacters
            || value.Any(character => character is < (char)0x21 or > (char)0x7e)
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || uri.Scheme is not "https" and not "file" and not "pkg" and not "urn")
        {
            return false;
        }

        return string.Equals(uri.AbsoluteUri, value, StringComparison.Ordinal);
    }

    private static bool IsSafeNormalized(string? value, int maximum)
    {
        return value is { Length: > 0 }
            && value.Length <= maximum
            && value.IsNormalized(NormalizationForm.FormC)
            && !value.Any(character => char.IsControl(character) || char.IsSurrogate(character));
    }

    private static bool IsCanonicalPath(string? value, int maximum)
    {
        if (string.IsNullOrEmpty(value) || value.Length > maximum || value[0] == '/' || value[^1] == '/')
        {
            return false;
        }

        var segments = value.Split('/');
        return segments.Length <= 8 && segments.All(IsCanonicalToken);
    }

    private static bool IsCanonicalToken(string value)
        => value.Length is >= 1 and <= 63
            && IsLowerAlphaNumeric(value[0])
            && IsLowerAlphaNumeric(value[^1])
            && value.All(character => IsLowerAlphaNumeric(character) || character is '-' or '_' or '.');

    private static bool IsLowerAlphaNumeric(char character) => character is >= 'a' and <= 'z' or >= '0' and <= '9';

    private static void ValidateComposition(GovernedLoopEffectAuthorityDecision decision, List<GovernedLoopEffectAuthorityValidationError> errors)
    {
        if (decision.AdmittedAuthority.GrantStatus != AuthorityGrantLifecycleStatus.Active
            || decision.AdmittedAuthority.GrantPosture != GovernedLoopEffectAuthorityGrantPosture.Active
            || decision.AdmittedAuthority.DependencyEvidenceHash is null
            || decision.AdmittedAuthority.ObservedCapabilityPins.Count != 0)
        {
            Add(errors, GovernedLoopEffectAuthorityValidationErrorCode.InvalidProof, "$.admittedAuthority.grantStatus");
            return;
        }

        if (decision.AdmittedAuthority.Ceiling.Capabilities.Any(identity => !decision.AdmittedAuthority.CapabilityPins.Any(pin => Equals(pin.DescriptorIdentity, identity))))
        {
            Add(errors, GovernedLoopEffectAuthorityValidationErrorCode.CapabilityMismatch, "$.admittedAuthority.capabilityPins");
        }

        if (decision.CurrentAuthority is { } current
            && current.ObservedCapabilityPins.Any(observed =>
            {
                var admitted = decision.AdmittedAuthority.CapabilityPins.FirstOrDefault(
                    pin => pin.DescriptorIdentity.Id.Equals(observed.DescriptorIdentity.Id));
                return admitted is null || Equals(admitted, observed);
            }))
        {
            Add(errors, GovernedLoopEffectAuthorityValidationErrorCode.CapabilityMismatch, "$.currentAuthority.observedCapabilityPins");
        }

        if (!IsEqualOrNarrow(decision.RequiredAuthority, decision.AdmittedAuthority.Ceiling))
        {
            Add(errors, GovernedLoopEffectAuthorityValidationErrorCode.AuthorityWidening, "$.requiredAuthority");
        }

        if (!PinsExactlyDescribeCeiling(decision.RequiredCapabilityPins, decision.RequiredAuthority)
            || !ArePinsSubset(decision.RequiredCapabilityPins, decision.AdmittedAuthority.CapabilityPins))
        {
            Add(errors, GovernedLoopEffectAuthorityValidationErrorCode.CapabilityMismatch, "$.requiredCapabilityPins");
        }

        if (decision.Disposition == GovernedLoopEffectAuthorityDisposition.Direct)
        {
            ValidateDirect(decision, errors);
            return;
        }

        if (!IsEmpty(decision.EffectiveAuthority))
        {
            Add(errors, GovernedLoopEffectAuthorityValidationErrorCode.InvalidComposition, "$.effectiveAuthority");
        }

        ValidateStopped(decision, errors);
    }

    private static void ValidateDirect(GovernedLoopEffectAuthorityDecision decision, List<GovernedLoopEffectAuthorityValidationError> errors)
    {
        var current = decision.CurrentAuthority;
        if (current is null)
        {
            Add(errors, GovernedLoopEffectAuthorityValidationErrorCode.InvalidComposition, "$.currentAuthority");
            return;
        }

        var exactBinding = HasExactGrantBinding(decision.AdmittedAuthority, current);
        if (!exactBinding)
        {
            Add(errors, GovernedLoopEffectAuthorityValidationErrorCode.BindingMismatch, "$.currentAuthority");
        }

        var ceilingNarrowed = IsEqualOrNarrow(current.Ceiling, decision.AdmittedAuthority.Ceiling);
        var pinsNarrowed = ArePinsSubset(current.CapabilityPins, decision.AdmittedAuthority.CapabilityPins);
        var boundaryNarrowed = DoesNotWiden(current.Boundary, decision.AdmittedAuthority.Boundary);
        if (!ceilingNarrowed || !boundaryNarrowed)
        {
            Add(errors, GovernedLoopEffectAuthorityValidationErrorCode.AuthorityWidening, "$.currentAuthority");
        }

        if (!pinsNarrowed)
        {
            Add(errors, GovernedLoopEffectAuthorityValidationErrorCode.CapabilityMismatch, "$.currentAuthority.capabilityPins");
        }

        if (current.GrantStatus != AuthorityGrantLifecycleStatus.Active
            || current.GrantPosture != GovernedLoopEffectAuthorityGrantPosture.Active
            || current.DependencyEvidenceHash is null
            || HasObservedRequiredDrift(decision.RequiredCapabilityPins, current.ObservedCapabilityPins)
            || !IsActiveAt(decision.AdmittedAuthority.Boundary, decision.EvaluatedAtUtc)
            || !IsActiveAt(current.Boundary, decision.EvaluatedAtUtc))
        {
            Add(errors, GovernedLoopEffectAuthorityValidationErrorCode.InvalidTimestamp, "$.evaluatedAtUtc");
        }

        if (!IsEqualOrNarrow(decision.RequiredAuthority, current.Ceiling))
        {
            Add(errors, GovernedLoopEffectAuthorityValidationErrorCode.AuthorityWidening, "$.requiredAuthority");
        }

        if (!AuthorityCeilingSubset.IsEqual(decision.EffectiveAuthority, decision.RequiredAuthority))
        {
            Add(errors, GovernedLoopEffectAuthorityValidationErrorCode.InvalidComposition, "$.effectiveAuthority");
        }

        if (!ArePinsSubset(decision.RequiredCapabilityPins, current.CapabilityPins)
            || !PinsExactlyDescribeCeiling(decision.RequiredCapabilityPins, decision.EffectiveAuthority))
        {
            Add(errors, GovernedLoopEffectAuthorityValidationErrorCode.CapabilityMismatch, "$.requiredCapabilityPins");
        }

        var isExact = AuthorityCeilingSubset.IsEqual(current.Ceiling, decision.AdmittedAuthority.Ceiling)
            && ArePinSetsEqual(current.CapabilityPins, decision.AdmittedAuthority.CapabilityPins)
            && current.ObservedCapabilityPins.Count == 0
            && Equals(current.Boundary, decision.AdmittedAuthority.Boundary);
        var expectedReason = isExact ? GovernedLoopEffectAuthorityReason.ActiveExact : GovernedLoopEffectAuthorityReason.ActiveNarrowed;
        if (decision.Reason != expectedReason)
        {
            Add(errors, GovernedLoopEffectAuthorityValidationErrorCode.InvalidComposition, "$.reason");
        }
    }

    private static void ValidateStopped(GovernedLoopEffectAuthorityDecision decision, List<GovernedLoopEffectAuthorityValidationError> errors)
    {
        if (decision.CurrentAuthority is null)
        {
            var valid = decision.Disposition switch
            {
                GovernedLoopEffectAuthorityDisposition.Pause => decision.Reason is GovernedLoopEffectAuthorityReason.GrantUnavailable
                    or GovernedLoopEffectAuthorityReason.GrantAmbiguous,
                GovernedLoopEffectAuthorityDisposition.Deny => decision.Reason is GovernedLoopEffectAuthorityReason.GrantMissing
                    or GovernedLoopEffectAuthorityReason.GrantInvalid
                    or GovernedLoopEffectAuthorityReason.InvalidRequest,
                _ => false
            };
            if (!valid)
            {
                Add(errors, GovernedLoopEffectAuthorityValidationErrorCode.InvalidComposition, "$.reason");
            }

            return;
        }

        if (decision.Disposition == GovernedLoopEffectAuthorityDisposition.Pause)
        {
            ValidatePauseWithCurrentProof(decision, errors);
            return;
        }

        if (decision.Disposition != GovernedLoopEffectAuthorityDisposition.Deny
            || decision.Reason != DetermineCurrentDenialReason(decision)
            || !DoesStoppedReasonMatchEvidence(decision))
        {
            Add(errors, GovernedLoopEffectAuthorityValidationErrorCode.InvalidComposition, "$.reason");
        }
    }

    private static void ValidatePauseWithCurrentProof(GovernedLoopEffectAuthorityDecision decision, List<GovernedLoopEffectAuthorityValidationError> errors)
    {
        var validReason = decision.Reason is GovernedLoopEffectAuthorityReason.CapabilityUnavailable
            or GovernedLoopEffectAuthorityReason.CapabilityAmbiguous
            or GovernedLoopEffectAuthorityReason.EvidenceUnavailable
            or GovernedLoopEffectAuthorityReason.EvidenceAmbiguous
            or GovernedLoopEffectAuthorityReason.EvidenceConflict;
        var current = decision.CurrentAuthority!;
        var capabilityPause = decision.Reason is GovernedLoopEffectAuthorityReason.CapabilityUnavailable
            or GovernedLoopEffectAuthorityReason.CapabilityAmbiguous;
        if (!validReason
            || current.GrantStatus != AuthorityGrantLifecycleStatus.Active
            || current.GrantPosture != GovernedLoopEffectAuthorityGrantPosture.Active
            || !HasExactGrantReferenceAndBinding(decision.AdmittedAuthority, current)
            || !IsActiveAt(decision.AdmittedAuthority.Boundary, decision.EvaluatedAtUtc)
            || !IsActiveAt(current.Boundary, decision.EvaluatedAtUtc)
            || !IsEqualOrNarrow(current.Ceiling, decision.AdmittedAuthority.Ceiling)
            || !DoesNotWiden(current.Boundary, decision.AdmittedAuthority.Boundary))
        {
            Add(errors, GovernedLoopEffectAuthorityValidationErrorCode.InvalidComposition, "$.reason");
            return;
        }

        if (!capabilityPause
            && !string.Equals(current.DependencyEvidenceHash, decision.AdmittedAuthority.DependencyEvidenceHash, StringComparison.Ordinal))
        {
            Add(errors, GovernedLoopEffectAuthorityValidationErrorCode.BindingMismatch, "$.currentAuthority.dependencyEvidenceHash");
        }

        if (decision.Reason is GovernedLoopEffectAuthorityReason.EvidenceUnavailable
            or GovernedLoopEffectAuthorityReason.EvidenceAmbiguous
            or GovernedLoopEffectAuthorityReason.EvidenceConflict)
        {
            if (!ArePinsSubset(decision.RequiredCapabilityPins, current.CapabilityPins)
                || !ArePinsSubset(current.CapabilityPins, decision.AdmittedAuthority.CapabilityPins)
                || HasObservedRequiredDrift(decision.RequiredCapabilityPins, current.ObservedCapabilityPins)
                || !IsEqualOrNarrow(decision.RequiredAuthority, current.Ceiling)
                || !string.Equals(current.DependencyEvidenceHash, decision.AdmittedAuthority.DependencyEvidenceHash, StringComparison.Ordinal))
            {
                Add(errors, GovernedLoopEffectAuthorityValidationErrorCode.CapabilityMismatch, "$.currentAuthority");
            }
        }
        else if (!ArePinsSubset(current.CapabilityPins, decision.AdmittedAuthority.CapabilityPins)
            || HasObservedRequiredDrift(decision.RequiredCapabilityPins, current.ObservedCapabilityPins))
        {
            Add(errors, GovernedLoopEffectAuthorityValidationErrorCode.CapabilityMismatch, "$.currentAuthority.capabilityPins");
        }
    }

    private static bool DoesStoppedReasonMatchEvidence(GovernedLoopEffectAuthorityDecision decision)
    {
        var admitted = decision.AdmittedAuthority;
        var current = decision.CurrentAuthority!;
        return decision.Reason switch
        {
            GovernedLoopEffectAuthorityReason.GrantNotEffective => current.GrantPosture == GovernedLoopEffectAuthorityGrantPosture.NotEffective
                && decision.EvaluatedAtUtc < current.Boundary.EffectiveAtUtc,
            GovernedLoopEffectAuthorityReason.GrantSuspended => current.GrantPosture == GovernedLoopEffectAuthorityGrantPosture.Suspended,
            GovernedLoopEffectAuthorityReason.GrantRevoked => current.GrantPosture == GovernedLoopEffectAuthorityGrantPosture.Revoked,
            GovernedLoopEffectAuthorityReason.GrantExpired => current.GrantPosture == GovernedLoopEffectAuthorityGrantPosture.Expired
                && (current.GrantStatus == AuthorityGrantLifecycleStatus.Expired
                    || current.Boundary.ExpiresAtUtc is { } expiry && expiry <= decision.EvaluatedAtUtc),
            GovernedLoopEffectAuthorityReason.GrantCompleted => current.GrantPosture == GovernedLoopEffectAuthorityGrantPosture.Completed
                && current.Boundary.CompletionConstraint == AuthorityGrantCompletionConstraintKind.FirstBoundRunCompletion,
            GovernedLoopEffectAuthorityReason.GrantStale => current.GrantPosture == GovernedLoopEffectAuthorityGrantPosture.Stale
                && !Equals(current.Grant, admitted.Grant),
            GovernedLoopEffectAuthorityReason.ProfileUnavailable => current.GrantPosture == GovernedLoopEffectAuthorityGrantPosture.ProfileUnavailable,
            GovernedLoopEffectAuthorityReason.RoleUnavailable => current.GrantPosture == GovernedLoopEffectAuthorityGrantPosture.RoleUnavailable,
            GovernedLoopEffectAuthorityReason.LoopUnavailable => current.GrantPosture == GovernedLoopEffectAuthorityGrantPosture.LoopUnavailable,
            GovernedLoopEffectAuthorityReason.CeilingExceeded => current.GrantPosture == GovernedLoopEffectAuthorityGrantPosture.CeilingExceeded,
            GovernedLoopEffectAuthorityReason.BindingMismatch => current.GrantPosture == GovernedLoopEffectAuthorityGrantPosture.Active
                && Equals(current.Grant, admitted.Grant)
                && !Equals(current.Binding, admitted.Binding),
            GovernedLoopEffectAuthorityReason.DependencyMismatch => current.GrantPosture == GovernedLoopEffectAuthorityGrantPosture.Active
                && Equals(current.Grant, admitted.Grant)
                && Equals(current.Binding, admitted.Binding)
                && current.DependencyEvidenceHash is not null
                && !string.Equals(current.DependencyEvidenceHash, admitted.DependencyEvidenceHash, StringComparison.Ordinal),
            GovernedLoopEffectAuthorityReason.CapabilityDrifted => current.GrantPosture == GovernedLoopEffectAuthorityGrantPosture.Active
                && decision.RequiredCapabilityPins.Any(required => current.ObservedCapabilityPins.Any(observed => observed.DescriptorIdentity.Id.Equals(required.DescriptorIdentity.Id) && !Equals(observed, required))),
            GovernedLoopEffectAuthorityReason.CapabilityInactive => current.GrantPosture == GovernedLoopEffectAuthorityGrantPosture.Active
                && decision.RequiredCapabilityPins.Any(required => !current.CapabilityPins.Any(pin => pin.DescriptorIdentity.Id.Equals(required.DescriptorIdentity.Id))
                    && !current.ObservedCapabilityPins.Any(pin => pin.DescriptorIdentity.Id.Equals(required.DescriptorIdentity.Id))),
            GovernedLoopEffectAuthorityReason.EffectOutsideCeiling => current.GrantPosture == GovernedLoopEffectAuthorityGrantPosture.Active
                && !IsEqualOrNarrow(decision.RequiredAuthority, current.Ceiling),
            _ => false
        };
    }

    private static GovernedLoopEffectAuthorityReason DetermineCurrentDenialReason(GovernedLoopEffectAuthorityDecision decision)
    {
        var admitted = decision.AdmittedAuthority;
        var current = decision.CurrentAuthority!;
        var postureReason = current.GrantPosture switch
        {
            GovernedLoopEffectAuthorityGrantPosture.NotEffective => GovernedLoopEffectAuthorityReason.GrantNotEffective,
            GovernedLoopEffectAuthorityGrantPosture.Suspended => GovernedLoopEffectAuthorityReason.GrantSuspended,
            GovernedLoopEffectAuthorityGrantPosture.Revoked => GovernedLoopEffectAuthorityReason.GrantRevoked,
            GovernedLoopEffectAuthorityGrantPosture.Expired => GovernedLoopEffectAuthorityReason.GrantExpired,
            GovernedLoopEffectAuthorityGrantPosture.Stale => GovernedLoopEffectAuthorityReason.GrantStale,
            GovernedLoopEffectAuthorityGrantPosture.ProfileUnavailable => GovernedLoopEffectAuthorityReason.ProfileUnavailable,
            GovernedLoopEffectAuthorityGrantPosture.RoleUnavailable => GovernedLoopEffectAuthorityReason.RoleUnavailable,
            GovernedLoopEffectAuthorityGrantPosture.LoopUnavailable => GovernedLoopEffectAuthorityReason.LoopUnavailable,
            GovernedLoopEffectAuthorityGrantPosture.CeilingExceeded => GovernedLoopEffectAuthorityReason.CeilingExceeded,
            GovernedLoopEffectAuthorityGrantPosture.Completed => GovernedLoopEffectAuthorityReason.GrantCompleted,
            _ => (GovernedLoopEffectAuthorityReason?)null
        };
        if (postureReason is not null)
        {
            return postureReason.Value;
        }

        if (!Equals(current.Grant, admitted.Grant))
        {
            return GovernedLoopEffectAuthorityReason.GrantStale;
        }

        if (!Equals(current.Binding, admitted.Binding))
        {
            return GovernedLoopEffectAuthorityReason.BindingMismatch;
        }

        if (!string.Equals(current.DependencyEvidenceHash, admitted.DependencyEvidenceHash, StringComparison.Ordinal))
        {
            return GovernedLoopEffectAuthorityReason.DependencyMismatch;
        }

        if (decision.RequiredCapabilityPins.Any(required => current.ObservedCapabilityPins.Any(pin => pin.DescriptorIdentity.Id.Equals(required.DescriptorIdentity.Id) && !Equals(pin, required))))
        {
            return GovernedLoopEffectAuthorityReason.CapabilityDrifted;
        }

        if (decision.RequiredCapabilityPins.Any(required => !current.CapabilityPins.Any(pin => pin.DescriptorIdentity.Id.Equals(required.DescriptorIdentity.Id))))
        {
            return GovernedLoopEffectAuthorityReason.CapabilityInactive;
        }

        if (!IsEqualOrNarrow(decision.RequiredAuthority, current.Ceiling))
        {
            return GovernedLoopEffectAuthorityReason.EffectOutsideCeiling;
        }

        return AuthorityCeilingSubset.IsEqual(current.Ceiling, admitted.Ceiling)
            && ArePinSetsEqual(current.CapabilityPins, admitted.CapabilityPins)
            && Equals(current.Boundary, admitted.Boundary)
            ? GovernedLoopEffectAuthorityReason.ActiveExact
            : GovernedLoopEffectAuthorityReason.ActiveNarrowed;
    }

    private static bool HasExactGrantBinding(GovernedLoopEffectAuthorityProof admitted, GovernedLoopEffectAuthorityProof current)
        => HasExactGrantReferenceAndBinding(admitted, current)
            && string.Equals(current.DependencyEvidenceHash, admitted.DependencyEvidenceHash, StringComparison.Ordinal);

    private static bool HasExactGrantReferenceAndBinding(GovernedLoopEffectAuthorityProof admitted, GovernedLoopEffectAuthorityProof current)
        => Equals(current.Grant, admitted.Grant)
            && Equals(current.Binding, admitted.Binding)
            && Equals(current.Boundary, admitted.Boundary);

    private static bool IsEqualOrNarrow(AuthorityCeiling candidate, AuthorityCeiling admitted)
        => AuthorityCeilingSubset.IsEqual(candidate, admitted) || AuthorityCeilingSubset.IsStrictSubset(candidate, admitted);

    private static bool DoesNotWiden(AuthorityGrantBoundary candidate, AuthorityGrantBoundary admitted)
        => candidate.EffectiveAtUtc >= admitted.EffectiveAtUtc
            && (admitted.ExpiresAtUtc is null || candidate.ExpiresAtUtc is not null && candidate.ExpiresAtUtc <= admitted.ExpiresAtUtc)
            && (admitted.CompletionConstraint != AuthorityGrantCompletionConstraintKind.FirstBoundRunCompletion
                || candidate.CompletionConstraint == AuthorityGrantCompletionConstraintKind.FirstBoundRunCompletion);

    private static bool IsActiveAt(AuthorityGrantBoundary boundary, DateTimeOffset evaluatedAtUtc)
        => evaluatedAtUtc >= boundary.EffectiveAtUtc
            && (boundary.ExpiresAtUtc is null || evaluatedAtUtc < boundary.ExpiresAtUtc);

    private static bool ArePinsSubset(IReadOnlyList<CapabilityAdmissionPin> candidate, IReadOnlyList<CapabilityAdmissionPin> admitted)
        => candidate.All(pin => admitted.Contains(pin));

    private static bool ArePinSetsEqual(IReadOnlyList<CapabilityAdmissionPin> left, IReadOnlyList<CapabilityAdmissionPin> right)
        => left.Count == right.Count && ArePinsSubset(left, right);

    private static bool HasObservedRequiredDrift(IReadOnlyList<CapabilityAdmissionPin> required, IReadOnlyList<CapabilityAdmissionPin> observed)
        => required.Any(requiredPin => observed.Any(observedPin => observedPin.DescriptorIdentity.Id.Equals(requiredPin.DescriptorIdentity.Id)));

    private static bool PinsExactlyDescribeCeiling(IReadOnlyList<CapabilityAdmissionPin> pins, AuthorityCeiling ceiling)
        => pins.Count == ceiling.Capabilities.Count
            && pins.All(pin => ceiling.Capabilities.Contains(pin.DescriptorIdentity));

    private static bool IsEmpty(AuthorityCeiling ceiling)
        => ceiling.Capabilities.Count == 0
            && ceiling.DataClasses.Count == 0
            && ceiling.MaxTargetCount == 0
            && ceiling.MaxSideEffectClass == CapabilitySideEffectClass.None
            && !ceiling.AllowsRecurrence
            && !ceiling.AllowsExternalPublication
            && !ceiling.AllowsIrreversibleAction;

    private static void ValidateSchema(int schemaVersion, string path, List<GovernedLoopEffectAuthorityValidationError> errors)
    {
        if (schemaVersion != GovernedLoopEffectAuthorityContractLimits.CurrentSchemaVersion)
        {
            Add(errors, GovernedLoopEffectAuthorityValidationErrorCode.UnsupportedSchemaVersion, path);
        }
    }

    private static void ValidateIdentifier(string? value, string path, List<GovernedLoopEffectAuthorityValidationError> errors)
    {
        if (!CustomLoopArtifactIdentifier.IsValid(value, GovernedLoopEffectAuthorityContractLimits.MaxIdentifierCharacters))
        {
            Add(errors, GovernedLoopEffectAuthorityValidationErrorCode.InvalidIdentity, path);
        }
    }

    private static void ValidatePositive(long value, long maximum, string path, List<GovernedLoopEffectAuthorityValidationError> errors)
    {
        if (value is < 1 || value > maximum)
        {
            Add(errors, GovernedLoopEffectAuthorityValidationErrorCode.LimitExceeded, path);
        }
    }

    private static void ValidateEnumeration<TValue>(TValue value, string path, List<GovernedLoopEffectAuthorityValidationError> errors)
        where TValue : struct, Enum
    {
        if (!IsSupported(value))
        {
            Add(errors, GovernedLoopEffectAuthorityValidationErrorCode.InvalidEnumeration, path);
        }
    }

    private static bool IsSupported<TValue>(TValue value)
        where TValue : struct, Enum
        => Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture) != 0 && Enum.IsDefined(value);

    private static void ValidateUtc(DateTimeOffset value, string path, List<GovernedLoopEffectAuthorityValidationError> errors)
    {
        if (!IsUtc(value))
        {
            Add(errors, GovernedLoopEffectAuthorityValidationErrorCode.InvalidTimestamp, path);
        }
    }

    private static bool IsUtc(DateTimeOffset value) => value != default && value.Offset == TimeSpan.Zero;

    private static void ValidateHash(string? value, string path, List<GovernedLoopEffectAuthorityValidationError> errors)
    {
        if (!IsHash(value))
        {
            Add(errors, GovernedLoopEffectAuthorityValidationErrorCode.InvalidHash, path);
        }
    }

    private static bool IsHash(string? value)
        => value?.Length == GovernedLoopEffectAuthorityContractLimits.Sha256HexCharacters
            && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsPrefixedSha256(string? value)
        => value?.Length == 71
            && value.StartsWith("sha256:", StringComparison.Ordinal)
            && value[7..].All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static GovernedLoopEffectAuthorityValidationResult Result(IEnumerable<GovernedLoopEffectAuthorityValidationError> errors)
        => GovernedLoopEffectAuthorityValidationResult.FromErrors(errors);

    private static void Add(List<GovernedLoopEffectAuthorityValidationError> errors, GovernedLoopEffectAuthorityValidationErrorCode code, string path)
    {
        if (errors.Count < GovernedLoopEffectAuthorityContractLimits.MaxValidationErrors)
        {
            errors.Add(GovernedLoopEffectAuthorityValidationError.Create(code, path));
        }
    }
}
