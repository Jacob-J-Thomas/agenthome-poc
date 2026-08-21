using EmbodySense.Core.Common.Authority.Delegation.Models;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Common.Authority.Delegation;

/// <summary>Validates bounded schema-1 delegated-authority evidence without authorizing its use.</summary>
public static class AuthorityDelegationContractValidator
{
    /// <summary>Validates one complete immutable delegation envelope and all canonical hashes.</summary>
    public static AuthorityDelegationContractValidationResult Validate(AuthorityDelegationEnvelope? envelope)
        => Safely(() =>
        {
            var errors = ValidateEnvelopeStructure(envelope);
            if (errors.Count == 0 && !AuthorityDelegationContractHash.Matches(envelope))
            {
                Add(errors, AuthorityDelegationContractValidationErrorCode.InvalidHash, "$.contentHash");
            }

            return Result(errors);
        });

    /// <summary>Validates one exact parent-evidence reference and its canonical hash.</summary>
    public static AuthorityDelegationContractValidationResult Validate(AuthorityDelegationParentEvidenceReference? parentEvidence)
        => Safely(() =>
        {
            var errors = ValidateParentEvidenceStructure(parentEvidence);
            if (errors.Count == 0 && !AuthorityDelegationContractHash.Matches(parentEvidence))
            {
                Add(errors, AuthorityDelegationContractValidationErrorCode.InvalidHash, "$.contentHash");
            }

            return Result(errors);
        });

    /// <summary>Validates one exact role, loop, or node target binding.</summary>
    public static AuthorityDelegationContractValidationResult Validate(AuthorityDelegationTargetBinding? target)
        => Safely(() => Result(ValidateTargetStructure(target)));

    /// <summary>Validates one local trusted-time and completion boundary.</summary>
    public static AuthorityDelegationContractValidationResult Validate(AuthorityDelegationBoundary? boundary)
        => Safely(() => Result(ValidateBoundaryStructure(boundary)));

    /// <summary>Validates one parent revocation/completion linkage and its canonical hash.</summary>
    public static AuthorityDelegationContractValidationResult Validate(AuthorityDelegationRevocationLink? revocationLink)
        => Safely(() =>
        {
            var errors = ValidateRevocationLinkStructure(revocationLink);
            if (errors.Count == 0 && !AuthorityDelegationContractHash.Matches(revocationLink))
            {
                Add(errors, AuthorityDelegationContractValidationErrorCode.InvalidHash, "$.linkageHash");
            }

            return Result(errors);
        });

    /// <summary>Validates one hash-only delegated-authority subset proof.</summary>
    public static AuthorityDelegationContractValidationResult Validate(AuthorityDelegationSubsetProof? proof)
        => Safely(() =>
        {
            var errors = ValidateSubsetProofStructure(proof);
            if (errors.Count == 0 && !AuthorityDelegationContractHash.Matches(proof))
            {
                Add(errors, AuthorityDelegationContractValidationErrorCode.InvalidHash, "$.contentHash");
            }

            return Result(errors);
        });

    internal static AuthorityDelegationContractValidationResult ValidateForHash(AuthorityDelegationEnvelope? envelope)
        => Safely(() => Result(ValidateEnvelopeStructure(envelope)));

    internal static AuthorityDelegationContractValidationResult ValidateForHash(AuthorityDelegationParentEvidenceReference? parentEvidence)
        => Safely(() => Result(ValidateParentEvidenceStructure(parentEvidence)));

    internal static AuthorityDelegationContractValidationResult ValidateForHash(AuthorityDelegationRevocationLink? revocationLink)
        => Safely(() => Result(ValidateRevocationLinkStructure(revocationLink)));

    internal static AuthorityDelegationContractValidationResult ValidateForHash(AuthorityDelegationSubsetProof? proof)
        => Safely(() => Result(ValidateSubsetProofStructure(proof)));

    internal static AuthorityDelegationContractValidationResult ValidateAuthorityScopeForHash(AuthorityCeiling? ceiling, IReadOnlyList<CapabilityAdmissionPin>? pins)
        => Safely(() =>
        {
            var errors = new List<AuthorityDelegationContractValidationError>();
            ValidateAuthorityScope(ceiling, pins, "$.authority", errors);
            return Result(errors);
        });

    private static List<AuthorityDelegationContractValidationError> ValidateEnvelopeStructure(AuthorityDelegationEnvelope? envelope)
    {
        var errors = new List<AuthorityDelegationContractValidationError>();
        if (envelope is null)
        {
            Add(errors, AuthorityDelegationContractValidationErrorCode.Required, "$");
            return errors;
        }

        if (envelope.SchemaVersion != AuthorityDelegationContractLimits.CurrentSchemaVersion)
        {
            Add(errors, AuthorityDelegationContractValidationErrorCode.UnsupportedSchema, "$.schemaVersion");
        }

        ValidateToken(envelope.EnvelopeId, AuthorityDelegationContractLimits.MaxIdentifierCharacters, "$.envelopeId", errors);
        AddNested(errors, ValidateParentEvidenceStructure(envelope.ParentEvidence), "$.parentEvidence");
        AddNested(errors, ValidateTargetStructure(envelope.Target), "$.target");
        ValidateAuthorityScope(envelope.DelegatedCeiling, envelope.DelegatedCapabilityPins, "$.delegatedAuthority", errors);
        ValidateToken(envelope.TargetClass, AuthorityDelegationContractLimits.MaxClassTokenCharacters, "$.targetClass", errors);
        ValidateToken(envelope.OperationClass, AuthorityDelegationContractLimits.MaxClassTokenCharacters, "$.operationClass", errors);
        if (envelope.Purpose is null || !AuthorityPurpose.TryParse(envelope.Purpose.Value, out var purpose, out _) || !purpose!.Equals(envelope.Purpose))
        {
            Add(errors, AuthorityDelegationContractValidationErrorCode.InvalidIdentity, "$.purpose");
        }

        AddNested(errors, ValidateBoundaryStructure(envelope.Boundary), "$.boundary");
        AddNested(errors, ValidateRevocationLinkStructure(envelope.RevocationLink), "$.revocationLink");
        AddNested(errors, ValidateSubsetProofStructure(envelope.SubsetProof), "$.subsetProof");
        ValidateUtc(envelope.IssuedAtUtc, "$.issuedAtUtc", errors);
        if (errors.Count == 0)
        {
            ValidateEnvelopeLinks(envelope, errors);
        }

        return errors;
    }

    private static void ValidateEnvelopeLinks(AuthorityDelegationEnvelope envelope, List<AuthorityDelegationContractValidationError> errors)
    {
        if (!AuthorityDelegationContractHash.Matches(envelope.ParentEvidence))
        {
            Add(errors, AuthorityDelegationContractValidationErrorCode.InvalidHash, "$.parentEvidence.contentHash");
        }

        if (!AuthorityDelegationContractHash.Matches(envelope.RevocationLink))
        {
            Add(errors, AuthorityDelegationContractValidationErrorCode.InvalidHash, "$.revocationLink.linkageHash");
        }

        if (!AuthorityDelegationContractHash.Matches(envelope.SubsetProof))
        {
            Add(errors, AuthorityDelegationContractValidationErrorCode.InvalidHash, "$.subsetProof.contentHash");
        }

        var parent = envelope.ParentEvidence;
        var link = envelope.RevocationLink;
        if (!Equals(parent.GrantReference, link.ParentGrant)
            || !string.Equals(parent.ParentAdmissionReceiptHash, link.ParentAdmissionReceiptHash, StringComparison.Ordinal)
            || !string.Equals(parent.WorkspaceId, link.WorkspaceId, StringComparison.Ordinal)
            || !string.Equals(parent.ParentExecution.RunId, link.ParentRunId, StringComparison.Ordinal)
            || parent.ParentExecution.ExecutionGeneration != link.ParentExecutionGeneration)
        {
            Add(errors, AuthorityDelegationContractValidationErrorCode.ParentLinkMismatch, "$.revocationLink");
        }

        if (!string.Equals(parent.ContentHash, envelope.SubsetProof.ParentEvidenceHash, StringComparison.Ordinal))
        {
            Add(errors, AuthorityDelegationContractValidationErrorCode.ParentLinkMismatch, "$.subsetProof.parentEvidenceHash");
        }

        var delegatedScopeHash = AuthorityDelegationContractHash.ComputeAuthorityScopeHash(envelope.DelegatedCeiling, envelope.DelegatedCapabilityPins);
        if (!string.Equals(delegatedScopeHash, envelope.SubsetProof.DelegatedAuthorityScopeHash, StringComparison.Ordinal))
        {
            Add(errors, AuthorityDelegationContractValidationErrorCode.CapabilityPinMismatch, "$.subsetProof.delegatedAuthorityScopeHash");
        }

        if (envelope.IssuedAtUtc < parent.EvaluatedAtUtc
            || envelope.Boundary.EffectiveAtUtc < envelope.IssuedAtUtc
            || envelope.Boundary.EffectiveAtUtc < parent.EvaluatedAtUtc)
        {
            Add(errors, AuthorityDelegationContractValidationErrorCode.InvalidBoundary, "$.boundary.effectiveAtUtc");
        }
    }

    private static List<AuthorityDelegationContractValidationError> ValidateParentEvidenceStructure(AuthorityDelegationParentEvidenceReference? parentEvidence)
    {
        var errors = new List<AuthorityDelegationContractValidationError>();
        if (parentEvidence is null)
        {
            Add(errors, AuthorityDelegationContractValidationErrorCode.Required, "$");
            return errors;
        }

        if (!ContextualRoleWorkspaceId.IsValid(parentEvidence.WorkspaceId))
        {
            Add(errors, AuthorityDelegationContractValidationErrorCode.InvalidIdentity, "$.workspaceId");
        }

        ValidateExecutionBinding(parentEvidence.ParentExecution, "$.parentExecution", errors);
        ValidateToken(parentEvidence.OriginNodeId, AuthorityDelegationContractLimits.MaxIdentifierCharacters, "$.originNodeId", errors);
        if (parentEvidence.OriginNodeAttempt is < 1 or > GovernedLoopExecutionLimits.MaxNodeAttempt)
        {
            Add(errors, AuthorityDelegationContractValidationErrorCode.BoundExceeded, "$.originNodeAttempt");
        }

        ValidateHash(parentEvidence.ParentAdmissionReceiptHash, "$.parentAdmissionReceiptHash", errors);
        if (parentEvidence.ActorId is null || !AuthorityActorId.TryParse(parentEvidence.ActorId.Value, out var actorId, out _) || !actorId!.Equals(parentEvidence.ActorId))
        {
            Add(errors, AuthorityDelegationContractValidationErrorCode.InvalidIdentity, "$.actorId");
        }

        ValidateGrantReference(parentEvidence.GrantReference, "$.grantReference", errors);
        ValidateGrantBinding(parentEvidence.GrantBinding, "$.grantBinding", errors);
        ValidateHash(parentEvidence.OriginBindingEvidenceHash, "$.originBindingEvidenceHash", errors);
        ValidateHash(parentEvidence.GrantDependencyEvidenceHash, "$.grantDependencyEvidenceHash", errors);
        ValidateUtc(parentEvidence.EvaluatedAtUtc, "$.evaluatedAtUtc", errors);
        return errors;
    }

    private static List<AuthorityDelegationContractValidationError> ValidateTargetStructure(AuthorityDelegationTargetBinding? target)
    {
        var errors = new List<AuthorityDelegationContractValidationError>();
        if (target is null)
        {
            Add(errors, AuthorityDelegationContractValidationErrorCode.Required, "$");
            return errors;
        }

        if (!Enum.IsDefined(target.Kind) || target.Kind == AuthorityDelegationTargetKind.Unknown)
        {
            Add(errors, AuthorityDelegationContractValidationErrorCode.InvalidEnumeration, "$.kind");
        }

        ValidateRolePin(target.Role, "$.role", errors);
        if (target.Loop is not null && !GovernedLoopRevisionContractValidator.Validate(target.Loop).IsValid)
        {
            Add(errors, AuthorityDelegationContractValidationErrorCode.InvalidIdentity, "$.loop");
        }

        var shapeIsValid = target.Kind switch
        {
            AuthorityDelegationTargetKind.Role => target.Loop is null && target.NodeId is null,
            AuthorityDelegationTargetKind.Loop => target.Loop is not null && target.NodeId is null,
            AuthorityDelegationTargetKind.Node => target.Loop is not null && IsToken(target.NodeId, AuthorityDelegationContractLimits.MaxIdentifierCharacters),
            _ => false,
        };
        if (!shapeIsValid)
        {
            Add(errors, AuthorityDelegationContractValidationErrorCode.InvalidTargetBinding, "$");
        }

        ValidateHash(target.BindingEvidenceHash, "$.bindingEvidenceHash", errors);
        return errors;
    }

    private static List<AuthorityDelegationContractValidationError> ValidateBoundaryStructure(AuthorityDelegationBoundary? boundary)
    {
        var errors = new List<AuthorityDelegationContractValidationError>();
        if (boundary is null)
        {
            Add(errors, AuthorityDelegationContractValidationErrorCode.Required, "$");
            return errors;
        }

        ValidateUtc(boundary.EffectiveAtUtc, "$.effectiveAtUtc", errors);
        if (boundary.ExpiresAtUtc is { } expiry)
        {
            ValidateUtc(expiry, "$.expiresAtUtc", errors);
            if (expiry <= boundary.EffectiveAtUtc)
            {
                Add(errors, AuthorityDelegationContractValidationErrorCode.InvalidBoundary, "$.expiresAtUtc");
            }
        }

        if (!Enum.IsDefined(boundary.CompletionConstraint) || boundary.CompletionConstraint == AuthorityDelegationCompletionConstraintKind.Unknown)
        {
            Add(errors, AuthorityDelegationContractValidationErrorCode.InvalidEnumeration, "$.completionConstraint");
        }

        if (boundary.ExpiresAtUtc is null && boundary.CompletionConstraint != AuthorityDelegationCompletionConstraintKind.TargetCompletion)
        {
            Add(errors, AuthorityDelegationContractValidationErrorCode.InvalidBoundary, "$");
        }

        return errors;
    }

    private static List<AuthorityDelegationContractValidationError> ValidateRevocationLinkStructure(AuthorityDelegationRevocationLink? link)
    {
        var errors = new List<AuthorityDelegationContractValidationError>();
        if (link is null)
        {
            Add(errors, AuthorityDelegationContractValidationErrorCode.Required, "$");
            return errors;
        }

        ValidateGrantReference(link.ParentGrant, "$.parentGrant", errors);
        ValidateHash(link.ParentAdmissionReceiptHash, "$.parentAdmissionReceiptHash", errors);
        if (!ContextualRoleWorkspaceId.IsValid(link.WorkspaceId))
        {
            Add(errors, AuthorityDelegationContractValidationErrorCode.InvalidIdentity, "$.workspaceId");
        }

        ValidateToken(link.ParentRunId, AuthorityDelegationContractLimits.MaxIdentifierCharacters, "$.parentRunId", errors);
        if (link.ParentExecutionGeneration is < 1 or > GovernedLoopExecutionLimits.MaxExecutionGeneration)
        {
            Add(errors, AuthorityDelegationContractValidationErrorCode.BoundExceeded, "$.parentExecutionGeneration");
        }

        return errors;
    }

    private static List<AuthorityDelegationContractValidationError> ValidateSubsetProofStructure(AuthorityDelegationSubsetProof? proof)
    {
        var errors = new List<AuthorityDelegationContractValidationError>();
        if (proof is null)
        {
            Add(errors, AuthorityDelegationContractValidationErrorCode.Required, "$");
            return errors;
        }

        ValidateHash(proof.ParentEvidenceHash, "$.parentEvidenceHash", errors);
        ValidateHash(proof.ParentAuthorityScopeHash, "$.parentAuthorityScopeHash", errors);
        ValidateHash(proof.DelegatedAuthorityScopeHash, "$.delegatedAuthorityScopeHash", errors);
        ValidateHash(proof.TargetMaximumEvidenceHash, "$.targetMaximumEvidenceHash", errors);
        if (!TrySnapshot(proof.NarrowingDimensions, AuthorityDelegationContractLimits.MaxNarrowingDimensions, out var dimensions)
            || dimensions!.Any(value => !Enum.IsDefined(value))
            || !IsStrictlyOrdered(dimensions!, value => (int)value, Comparer<int>.Default))
        {
            Add(errors, AuthorityDelegationContractValidationErrorCode.InvalidCollection, "$.narrowingDimensions");
        }

        return errors;
    }

    private static void ValidateAuthorityScope(
        AuthorityCeiling? ceiling,
        IReadOnlyList<CapabilityAdmissionPin>? pins,
        string path,
        List<AuthorityDelegationContractValidationError> errors)
    {
        if (!AuthorityProfileValidator.ValidateCeiling(ceiling).IsValid)
        {
            Add(errors, AuthorityDelegationContractValidationErrorCode.AuthorityWidening, path + ".ceiling");
            return;
        }

        if (!TrySnapshot(ceiling!.Capabilities, AuthorityContractLimits.MaxCapabilitiesPerCeiling, out var capabilities)
            || !TrySnapshot(ceiling.DataClasses, AuthorityContractLimits.MaxDataClassesPerCeiling, out var dataClasses)
            || !IsStrictlyOrdered(capabilities!, CapabilityIdentityKey, StringComparer.Ordinal)
            || !IsStrictlyOrdered(dataClasses!, value => value.Value, StringComparer.Ordinal))
        {
            Add(errors, AuthorityDelegationContractValidationErrorCode.InvalidCollection, path + ".ceiling");
        }

        if (!TrySnapshot(pins, CapabilityContractLimits.MaxCapabilityAdmissionPins, out var pinSnapshot)
            || pinSnapshot!.Any(pin => !IsValidPin(pin))
            || !IsStrictlyOrdered(pinSnapshot!, pin => CapabilityIdentityKey(pin.DescriptorIdentity), StringComparer.Ordinal))
        {
            Add(errors, AuthorityDelegationContractValidationErrorCode.InvalidCollection, path + ".pins");
            return;
        }

        if (capabilities is null
            || pinSnapshot!.Count != capabilities.Count
            || !pinSnapshot.Select(pin => pin.DescriptorIdentity).SequenceEqual(capabilities))
        {
            Add(errors, AuthorityDelegationContractValidationErrorCode.CapabilityPinMismatch, path + ".pins");
        }
    }

    private static void ValidateExecutionBinding(GovernedLoopExecutionBinding? binding, string path, List<AuthorityDelegationContractValidationError> errors)
    {
        try
        {
            if (binding is null
                || GovernedLoopExecutionBinding.Create(binding.SchemaVersion, binding.RunId, binding.Revision, binding.ExecutionGeneration) != binding)
            {
                Add(errors, AuthorityDelegationContractValidationErrorCode.InvalidIdentity, path);
            }
        }
        catch (Exception)
        {
            Add(errors, AuthorityDelegationContractValidationErrorCode.InvalidIdentity, path);
        }
    }

    private static void ValidateGrantReference(AuthorityGrantReference? reference, string path, List<AuthorityDelegationContractValidationError> errors)
    {
        if (reference?.GrantId is null
            || reference.Revision is null
            || !AuthorityGrantId.TryParse(reference.GrantId.Value, out _, out _)
            || !AuthorityGrantRevision.TryParse(reference.Revision.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), out _, out _)
            || reference.ContentHash is not { Length: 71 }
            || !reference.ContentHash.StartsWith("sha256:", StringComparison.Ordinal)
            || reference.ContentHash[7..].Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            Add(errors, AuthorityDelegationContractValidationErrorCode.InvalidIdentity, path);
        }
    }

    private static void ValidateGrantBinding(AuthorityGrantBinding? binding, string path, List<AuthorityDelegationContractValidationError> errors)
    {
        if (binding?.Profile?.Reference?.ProfileId is null
            || binding.Profile.Reference.Revision is null
            || binding.Profile.ContentHash is null
            || !AuthorityProfileId.TryParse(binding.Profile.Reference.ProfileId.Value, out _, out _)
            || !AuthorityProfileRevision.TryParse(binding.Profile.Reference.Revision.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), out _, out _)
            || !AuthorityProfileHash.TryParse(binding.Profile.ContentHash.Value, out _, out _))
        {
            Add(errors, AuthorityDelegationContractValidationErrorCode.InvalidIdentity, path + ".profile");
        }

        ValidateRolePin(binding?.Role, path + ".role", errors);
        if (binding?.Loop is null || !GovernedLoopRevisionContractValidator.Validate(binding.Loop).IsValid)
        {
            Add(errors, AuthorityDelegationContractValidationErrorCode.InvalidIdentity, path + ".loop");
        }
    }

    private static void ValidateRolePin(ContextualRoleRevisionPin? role, string path, List<AuthorityDelegationContractValidationError> errors)
    {
        if (role?.Identity is null
            || !ContextualRoleId.IsValid(role.Identity.RoleId)
            || role.Identity.Revision < 1
            || !AuthorityDelegationContractHash.IsCanonicalHash(role.ContentHash))
        {
            Add(errors, AuthorityDelegationContractValidationErrorCode.InvalidIdentity, path);
        }
    }

    private static bool IsValidPin(CapabilityAdmissionPin? pin)
    {
        if (pin?.DescriptorIdentity?.Id is null
            || pin.DescriptorIdentity.Version is null
            || pin.DescriptorIdentity.Hash is null
            || !CapabilityId.TryParse(pin.DescriptorIdentity.Id.Value, out _, out _)
            || !CapabilityVersion.TryParse(pin.DescriptorIdentity.Version.Value, out _, out _)
            || !CapabilityDescriptorHash.TryParse(pin.DescriptorIdentity.Hash.Value, out _, out _)
            || !Enum.IsDefined(pin.Kind)
            || pin.Kind == CapabilityKind.Unknown
            || pin.Implementation?.ProviderId is null
            || !CapabilityProviderId.TryParse(pin.Implementation.ProviderId.Value, out _, out _)
            || !CapabilityIdentifierRules.IsPath(pin.Implementation.ImplementationId, CapabilityContractLimits.MaxImplementationIdCharacters)
            || pin.Provenance is null
            || !Enum.IsDefined(pin.Provenance.Kind)
            || pin.Provenance.Kind == CapabilityProvenanceKind.Unknown
            || !IsSafeSourceUri(pin.Provenance.SourceUri)
            || pin.Provenance.SourceRevision is not null && !IsSafeSourceRevision(pin.Provenance.SourceRevision)
            || pin.Provenance.Integrity is not null && !CapabilityIntegrityDigest.TryParse(pin.Provenance.Integrity.Value, out _, out _)
            || pin.Provenance.Kind == CapabilityProvenanceKind.RemoteArtifact && pin.Provenance.Integrity is null
            || pin.Artifact is null
            || pin.Artifact.Checksum is not null && !CapabilityIntegrityDigest.TryParse(pin.Artifact.Checksum.Value, out _, out _)
            || pin.Artifact.Signature is not null && !CapabilityTextRules.IsSafeAsciiToken(pin.Artifact.Signature, CapabilityContractLimits.MaxArtifactSignatureCharacters)
            || !CapabilityTextRules.IsSafeNormalized(pin.SafeDescription, CapabilityContractLimits.MaxPurposeCharacters, allowEmpty: false))
        {
            return false;
        }

        return true;
    }

    private static bool IsSafeSourceUri(string? value)
    {
        return CapabilityTextRules.IsSafeAsciiToken(value, CapabilityContractLimits.MaxSourceUriCharacters)
            && Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && string.IsNullOrEmpty(uri.UserInfo)
            && string.IsNullOrEmpty(uri.Query)
            && string.IsNullOrEmpty(uri.Fragment)
            && uri.Scheme is "https" or "file" or "pkg" or "urn"
            && string.Equals(uri.AbsoluteUri, value, StringComparison.Ordinal);
    }

    private static bool IsSafeSourceRevision(string value)
        => value.Length is >= 1 and <= CapabilityContractLimits.MaxSourceRevisionCharacters
            && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or '/' or '@');

    private static void ValidateToken(string? value, int maximum, string path, List<AuthorityDelegationContractValidationError> errors)
    {
        if (!IsToken(value, maximum))
        {
            Add(errors, AuthorityDelegationContractValidationErrorCode.InvalidIdentity, path);
        }
    }

    private static bool IsToken(string? value, int maximum) => AuthorityTextRules.IsToken(value, maximum);

    private static void ValidateHash(string? value, string path, List<AuthorityDelegationContractValidationError> errors)
    {
        if (!AuthorityDelegationContractHash.IsCanonicalHash(value))
        {
            Add(errors, AuthorityDelegationContractValidationErrorCode.InvalidHash, path);
        }
    }

    private static void ValidateUtc(DateTimeOffset value, string path, List<AuthorityDelegationContractValidationError> errors)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            Add(errors, AuthorityDelegationContractValidationErrorCode.InvalidBoundary, path);
        }
    }

    private static bool TrySnapshot<TValue>(IReadOnlyList<TValue>? values, int maximum, out IReadOnlyList<TValue>? snapshot)
    {
        snapshot = AuthorityDelegationContractCopy.Snapshot(values, maximum);
        return snapshot is not null;
    }

    private static bool IsStrictlyOrdered<TValue, TKey>(IReadOnlyList<TValue> values, Func<TValue, TKey> keySelector, IComparer<TKey> comparer)
    {
        for (var index = 1; index < values.Count; index++)
        {
            if (comparer.Compare(keySelector(values[index - 1]), keySelector(values[index])) >= 0)
            {
                return false;
            }
        }

        return true;
    }

    private static string CapabilityIdentityKey(CapabilityDescriptorIdentity value)
        => string.Concat(value.Id.Value, "\0", value.Version.Value, "\0", value.Hash.Value);

    private static void AddNested(
        List<AuthorityDelegationContractValidationError> target,
        IReadOnlyList<AuthorityDelegationContractValidationError> nested,
        string prefix)
    {
        foreach (var error in nested)
        {
            Add(target, error.Code, prefix + (error.Path == "$" ? string.Empty : error.Path[1..]));
        }
    }

    private static void Add(List<AuthorityDelegationContractValidationError> errors, AuthorityDelegationContractValidationErrorCode code, string path)
    {
        if (errors.Count >= AuthorityDelegationContractLimits.MaxValidationErrors)
        {
            return;
        }

        var safePath = path.Length <= AuthorityDelegationContractLimits.MaxErrorPathCharacters ? path : path[..AuthorityDelegationContractLimits.MaxErrorPathCharacters];
        var error = new AuthorityDelegationContractValidationError(code, safePath);
        if (!errors.Contains(error))
        {
            errors.Add(error);
        }
    }

    private static AuthorityDelegationContractValidationResult Result(IReadOnlyList<AuthorityDelegationContractValidationError> errors)
        => new(errors, errors.Count == 0);

    private static AuthorityDelegationContractValidationResult Safely(Func<AuthorityDelegationContractValidationResult> validation)
    {
        try
        {
            return validation();
        }
        catch (Exception)
        {
            return Result([new AuthorityDelegationContractValidationError(AuthorityDelegationContractValidationErrorCode.InvalidComposition, "$")]);
        }
    }
}
