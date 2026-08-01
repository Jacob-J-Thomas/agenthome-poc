using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Credentials.Models;

namespace EmbodySense.Core.Common.Credentials;

/// <summary>Validates schema-1 credential contracts without resolving or granting a credential.</summary>
public static class CredentialContractValidator
{
    private static readonly HashSet<string> _metadataAllowlist = new(StringComparer.Ordinal) { "account-label", "display-name", "environment", "service" };

    /// <summary>Validates safe public credential-reference metadata.</summary>
    public static CredentialContractValidationResult Validate(CredentialReference? reference)
    {
        var errors = new List<CredentialContractError>();
        if (reference is null)
        {
            errors.Add(Error(CredentialContractErrorCode.CredentialReferenceRequired, "$"));
            return CredentialContractValidationResult.FromErrors(errors);
        }

        RequireSchema(reference.SchemaVersion, errors);
        Require(reference.Id is not null, CredentialContractErrorCode.InvalidReferenceId, "$.id", errors);
        Require(CredentialContractText.IsToken(reference.Type), CredentialContractErrorCode.InvalidReferenceType, "$.type", errors);
        Require(Enum.IsDefined(reference.Status), CredentialContractErrorCode.InvalidLifecycleStatus, "$.status", errors);
        Require(CredentialContractText.IsToken(reference.OwnerId, CredentialContractLimits.MaxIdCharacters), CredentialContractErrorCode.InvalidOwnerId, "$.ownerId", errors);
        Require(CredentialContractText.IsSafeText(reference.Purpose, CredentialContractLimits.MaxPurposeCharacters), CredentialContractErrorCode.InvalidPurpose, "$.purpose", errors);
        Require(reference.ProviderId is not null, CredentialContractErrorCode.InvalidProviderId, "$.providerId", errors);
        Require(CredentialContractText.IsUtc(reference.CreatedAtUtc), CredentialContractErrorCode.InvalidTimestamp, "$.createdAtUtc", errors);
        Require(CredentialContractText.IsUtc(reference.UpdatedAtUtc) && reference.UpdatedAtUtc >= reference.CreatedAtUtc, CredentialContractErrorCode.InvalidTimestamp, "$.updatedAtUtc", errors);
        Require(reference.ExpiresAtUtc is null || CredentialContractText.IsUtc(reference.ExpiresAtUtc.Value) && reference.ExpiresAtUtc > reference.CreatedAtUtc, CredentialContractErrorCode.InvalidExpiry, "$.expiresAtUtc", errors);
        ValidateMetadata(reference.Metadata, errors);
        return CredentialContractValidationResult.FromErrors(errors);
    }

    /// <summary>Validates a credential scope and all ambiguity constraints.</summary>
    public static CredentialContractValidationResult Validate(CredentialScope? scope)
    {
        var errors = new List<CredentialContractError>();
        if (scope is null)
        {
            errors.Add(Error(CredentialContractErrorCode.CredentialScopeRequired, "$"));
            return CredentialContractValidationResult.FromErrors(errors);
        }

        RequireToken(scope.WorkspaceId, "$.workspaceId", required: true, errors);
        RequireToken(scope.RoleId, "$.roleId", required: false, errors);
        RequireToken(scope.LoopId, "$.loopId", required: false, errors);
        Require(scope.LoopId is null || scope.RoleId is not null, CredentialContractErrorCode.AmbiguousLoopScope, "$.loopId", errors);
        Require(scope.LoopRevision is null || scope.LoopRevision >= 0 && scope.LoopId is not null, CredentialContractErrorCode.InvalidLoopRevision, "$.loopRevision", errors);
        RequireToken(scope.NodeId, "$.nodeId", required: false, errors);
        Require(scope.NodeId is null || scope.LoopId is not null, CredentialContractErrorCode.AmbiguousNodeScope, "$.nodeId", errors);
        Require(scope.Capability is null == (scope.Implementation is null), CredentialContractErrorCode.AmbiguousCapabilityScope, "$.capability", errors);
        if (scope.Capability is not null)
        {
            ValidateCapability(scope.Capability, scope.Implementation!, errors, "$.capability");
        }

        RequireToken(scope.Service, "$.service", required: false, errors);
        RequireToken(scope.Target, "$.target", required: false, errors);
        Require(scope.Target is null || scope.Service is not null, CredentialContractErrorCode.AmbiguousTargetScope, "$.target", errors);
        RequireToken(scope.OperationClass, "$.operationClass", required: false, errors);
        RequireToken(scope.ActorId, "$.actorId", required: false, errors);
        Require(scope.NotBeforeUtc is null || CredentialContractText.IsUtc(scope.NotBeforeUtc.Value), CredentialContractErrorCode.InvalidTimestamp, "$.notBeforeUtc", errors);
        Require(scope.NotAfterUtc is null || CredentialContractText.IsUtc(scope.NotAfterUtc.Value), CredentialContractErrorCode.InvalidTimestamp, "$.notAfterUtc", errors);
        Require(scope.NotBeforeUtc is null || scope.NotAfterUtc is null || scope.NotBeforeUtc < scope.NotAfterUtc, CredentialContractErrorCode.EmptyTimeScope, "$.notAfterUtc", errors);
        return CredentialContractValidationResult.FromErrors(errors);
    }

    /// <summary>Validates an exact reference-to-capability binding.</summary>
    public static CredentialContractValidationResult Validate(CredentialCapabilityBinding? binding)
    {
        var errors = new List<CredentialContractError>();
        if (binding is null)
        {
            errors.Add(Error(CredentialContractErrorCode.CredentialBindingRequired, "$"));
            return CredentialContractValidationResult.FromErrors(errors);
        }

        RequireSchema(binding.SchemaVersion, errors);
        Require(binding.ReferenceId is not null, CredentialContractErrorCode.InvalidReferenceId, "$.referenceId", errors);
        Require(binding.Requirement is not null, CredentialContractErrorCode.InvalidSecretRequirement, "$.requirement", errors);
        ValidateCapability(binding.Capability, binding.Implementation, errors, "$.capability");
        Merge(Validate(binding.Scope), "$.scope", errors);
        Require(binding.Scope is not null && binding.Scope.Capability is not null && Equals(binding.Scope.Capability, binding.Capability) && Equals(binding.Scope.Implementation, binding.Implementation), CredentialContractErrorCode.BindingScopeMismatch, "$.scope.capability", errors);
        return CredentialContractValidationResult.FromErrors(errors);
    }

    /// <summary>Validates the bounded public shape of an authority proof without authenticating it.</summary>
    public static CredentialContractValidationResult Validate(CredentialAuthorityProof? proof)
    {
        var errors = new List<CredentialContractError>();
        if (proof is null)
        {
            errors.Add(Error(CredentialContractErrorCode.CredentialAuthorityProofRequired, "$"));
            return CredentialContractValidationResult.FromErrors(errors);
        }

        RequireSchema(proof.SchemaVersion, errors);
        Require(proof.ProofId is not null, CredentialContractErrorCode.InvalidProofId, "$.proofId", errors);
        Require(proof.ReferenceId is not null, CredentialContractErrorCode.InvalidReferenceId, "$.referenceId", errors);
        Require(proof.BindingHash is not null, CredentialContractErrorCode.InvalidBindingHash, "$.bindingHash", errors);
        Merge(Validate(proof.GrantedScope), "$.grantedScope", errors);
        RequireToken(proof.ActorId, "$.actorId", required: true, errors);
        Require(proof.GrantedScope is not null && proof.GrantedScope.ActorId is not null && string.Equals(proof.ActorId, proof.GrantedScope.ActorId, StringComparison.Ordinal), CredentialContractErrorCode.ProofActorMismatch, "$.actorId", errors);
        Require(proof.RunId is not null, CredentialContractErrorCode.InvalidRunId, "$.runId", errors);
        Require(proof.AuthorityRevision >= 0, CredentialContractErrorCode.InvalidAuthorityRevision, "$.authorityRevision", errors);
        Require(CredentialContractText.IsUtc(proof.IssuedAtUtc) && CredentialContractText.IsUtc(proof.ExpiresAtUtc) && proof.ExpiresAtUtc > proof.IssuedAtUtc && proof.ExpiresAtUtc - proof.IssuedAtUtc <= CredentialContractLimits.MaxProofLifetime, CredentialContractErrorCode.InvalidProofLifetime, "$.expiresAtUtc", errors);
        Require(proof.IssuerId is not null, CredentialContractErrorCode.InvalidIssuerId, "$.issuerId", errors);
        Require(proof.Authenticator is not null, CredentialContractErrorCode.InvalidAuthenticator, "$.authenticator", errors);
        return CredentialContractValidationResult.FromErrors(errors);
    }

    /// <summary>Validates value-free credential-use evidence.</summary>
    public static CredentialContractValidationResult Validate(CredentialUseEvidence? evidence)
    {
        var errors = new List<CredentialContractError>();
        if (evidence is null)
        {
            errors.Add(Error(CredentialContractErrorCode.CredentialUseEvidenceRequired, "$"));
            return CredentialContractValidationResult.FromErrors(errors);
        }

        RequireSchema(evidence.SchemaVersion, errors);
        Require(evidence.EvidenceId is not null && evidence.ReferenceId is not null && evidence.BindingHash is not null && evidence.ProofId is not null && evidence.RunId is not null, CredentialContractErrorCode.InvalidEvidenceIdentity, "$", errors);
        Merge(Validate(evidence.UsedScope), "$.usedScope", errors);
        Require(CredentialContractText.IsUtc(evidence.UsedAtUtc), CredentialContractErrorCode.InvalidTimestamp, "$.usedAtUtc", errors);
        Require(Enum.IsDefined(evidence.Outcome), CredentialContractErrorCode.InvalidUseOutcome, "$.outcome", errors);
        Require(evidence.RedactionApplied, CredentialContractErrorCode.RedactionNotApplied, "$.redactionApplied", errors);
        return CredentialContractValidationResult.FromErrors(errors);
    }

    /// <summary>Validates exact binding, proof, scope, and time relationships at verifier-observed trusted UTC.</summary>
    public static CredentialContractValidationResult Validate(CredentialUseRequest? request, DateTimeOffset observedAtUtc)
    {
        var errors = new List<CredentialContractError>();
        if (request is null)
        {
            errors.Add(Error(CredentialContractErrorCode.CredentialUseRequestRequired, "$"));
            return CredentialContractValidationResult.FromErrors(errors);
        }

        Merge(Validate(request.Binding), "$.binding", errors);
        Merge(Validate(request.RequestedScope), "$.requestedScope", errors);
        Merge(Validate(request.AuthorityProof), "$.authorityProof", errors);
        Require(CredentialContractText.IsUtc(observedAtUtc), CredentialContractErrorCode.InvalidTimestamp, "$.observedAtUtc", errors);
        if (request.Binding is not null && request.BindingHash is not null && request.AuthorityProof is not null && CredentialContractJson.TryHash(request.Binding, out var bindingHash, out _))
        {
            Require(bindingHash!.FixedTimeEquals(request.BindingHash), CredentialContractErrorCode.BindingHashMismatch, "$.bindingHash", errors);
            Require(bindingHash.FixedTimeEquals(request.AuthorityProof.BindingHash), CredentialContractErrorCode.ProofBindingMismatch, "$.authorityProof.bindingHash", errors);
        }
        else
        {
            Require(false, CredentialContractErrorCode.InvalidBindingHash, "$.bindingHash", errors);
        }

        Require(request.Binding?.ReferenceId is not null && request.AuthorityProof?.ReferenceId is not null && request.Binding.ReferenceId.Equals(request.AuthorityProof.ReferenceId), CredentialContractErrorCode.ProofReferenceMismatch, "$.authorityProof.referenceId", errors);
        Require(request.Binding?.Scope is not null && request.AuthorityProof?.GrantedScope is not null && request.RequestedScope is not null && CredentialScopeRules.IsNarrowerThanOrEqual(request.RequestedScope, request.Binding.Scope) && CredentialScopeRules.IsNarrowerThanOrEqual(request.RequestedScope, request.AuthorityProof.GrantedScope), CredentialContractErrorCode.CredentialScopeMismatch, "$.requestedScope", errors);
        Require(request.AuthorityProof is not null && observedAtUtc >= request.AuthorityProof.IssuedAtUtc && observedAtUtc < request.AuthorityProof.ExpiresAtUtc, CredentialContractErrorCode.CredentialProofExpired, "$.observedAtUtc", errors);
        Require(request.RequestedScope is not null && (request.RequestedScope.NotBeforeUtc is null || observedAtUtc >= request.RequestedScope.NotBeforeUtc) && (request.RequestedScope.NotAfterUtc is null || observedAtUtc < request.RequestedScope.NotAfterUtc), CredentialContractErrorCode.CredentialRequestedOutsideScope, "$.observedAtUtc", errors);
        return CredentialContractValidationResult.FromErrors(errors);
    }

    private static void ValidateMetadata(IReadOnlyDictionary<string, string>? metadata, List<CredentialContractError> errors)
    {
        if (metadata is null || metadata.Count > CredentialContractLimits.MaxMetadataEntries)
        {
            errors.Add(Error(CredentialContractErrorCode.InvalidMetadata, "$.metadata"));
            return;
        }

        foreach (var pair in metadata)
        {
            Require(_metadataAllowlist.Contains(pair.Key), CredentialContractErrorCode.MetadataKeyNotAllowed, "$.metadata", errors);
            Require(CredentialContractText.IsSafeText(pair.Value, CredentialContractLimits.MaxMetadataValueCharacters), CredentialContractErrorCode.InvalidMetadataValue, "$.metadata", errors);
        }
    }

    private static void ValidateCapability(Capabilities.CapabilityDescriptorIdentity? capability, CapabilityImplementationIdentity? implementation, List<CredentialContractError> errors, string path)
    {
        Require(capability?.Id is not null && capability.Version is not null && capability.Hash is not null, CredentialContractErrorCode.InvalidCapabilityIdentity, path, errors);
        Require(implementation is { ProviderId: not null } && CapabilityIdentifierRules.IsProviderId(implementation.ProviderId.Value) && CapabilityIdentifierRules.IsPath(implementation.ImplementationId, CapabilityContractLimits.MaxImplementationIdCharacters), CredentialContractErrorCode.InvalidCapabilityImplementation, path + ".implementation", errors);
    }

    private static void RequireSchema(int schemaVersion, List<CredentialContractError> errors) => Require(schemaVersion == 1, CredentialContractErrorCode.UnsupportedSchemaVersion, "$.schemaVersion", errors);

    private static void RequireToken(string? value, string path, bool required, List<CredentialContractError> errors)
    {
        Require(!required && value is null || CredentialContractText.IsToken(value, CredentialContractLimits.MaxIdCharacters), CredentialContractErrorCode.InvalidScopeDimension, path, errors);
    }

    private static void Merge(CredentialContractValidationResult validation, string prefix, List<CredentialContractError> errors)
    {
        errors.AddRange(validation.Errors.Select(error => CredentialContractError.Create(error.Code, error.Path == "$" ? prefix : prefix + error.Path[1..])));
    }

    private static void Require(bool condition, CredentialContractErrorCode code, string path, List<CredentialContractError> errors)
    {
        if (!condition)
        {
            errors.Add(Error(code, path));
        }
    }

    private static CredentialContractError Error(CredentialContractErrorCode code, string path) => CredentialContractError.Create(code, path);
}
