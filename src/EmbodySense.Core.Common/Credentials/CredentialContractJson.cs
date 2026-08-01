using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Credentials.Models;

namespace EmbodySense.Core.Common.Credentials;

/// <summary>Serializes and parses closed, deterministic schema-version-1 credential contracts.</summary>
public static class CredentialContractJson
{
    private static readonly JsonSerializerOptions _options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 24
    };

    /// <summary>Serializes a validated public credential reference.</summary>
    public static bool TrySerialize(CredentialReference? reference, out string? json, out CredentialContractValidationResult validation)
    {
        json = null;
        validation = CredentialContractValidator.Validate(reference);
        return validation.IsValid && TrySerialize(ToDto(reference!), out json, ref validation);
    }

    /// <summary>Parses exact canonical public credential-reference JSON.</summary>
    public static bool TryDeserializeReference(string? json, out CredentialReference? reference, out CredentialContractValidationResult validation)
    {
        reference = null;
        if (!TryDeserialize<ReferenceDto>(json, out var dto, out validation) || !TryBuild(dto!, out reference, out validation) || !TrySerialize(reference, out var canonical, out validation))
        {
            return false;
        }

        return RequireCanonical(json!, canonical!, ref reference, out validation);
    }

    /// <summary>Serializes a validated credential scope.</summary>
    public static bool TrySerialize(CredentialScope? scope, out string? json, out CredentialContractValidationResult validation)
    {
        json = null;
        validation = CredentialContractValidator.Validate(scope);
        return validation.IsValid && TrySerialize(ToDto(scope!), out json, ref validation);
    }

    /// <summary>Parses exact canonical credential-scope JSON.</summary>
    public static bool TryDeserializeScope(string? json, out CredentialScope? scope, out CredentialContractValidationResult validation)
    {
        scope = null;
        if (!TryDeserialize<ScopeDto>(json, out var dto, out validation) || !TryBuild(dto!, out scope, out validation) || !TrySerialize(scope, out var canonical, out validation))
        {
            return false;
        }

        return RequireCanonical(json!, canonical!, ref scope, out validation);
    }

    /// <summary>Serializes a validated exact credential capability binding.</summary>
    public static bool TrySerialize(CredentialCapabilityBinding? binding, out string? json, out CredentialContractValidationResult validation)
    {
        json = null;
        validation = CredentialContractValidator.Validate(binding);
        return validation.IsValid && TrySerialize(ToDto(binding!), out json, ref validation);
    }

    /// <summary>Parses exact canonical credential-binding JSON.</summary>
    public static bool TryDeserializeBinding(string? json, out CredentialCapabilityBinding? binding, out CredentialContractValidationResult validation)
    {
        binding = null;
        if (!TryDeserialize<BindingDto>(json, out var dto, out validation) || !TryBuild(dto!, out binding, out validation) || !TrySerialize(binding, out var canonical, out validation))
        {
            return false;
        }

        return RequireCanonical(json!, canonical!, ref binding, out validation);
    }

    /// <summary>Serializes the public shape of a validated authority proof.</summary>
    public static bool TrySerialize(CredentialAuthorityProof? proof, out string? json, out CredentialContractValidationResult validation)
    {
        json = null;
        validation = CredentialContractValidator.Validate(proof);
        return validation.IsValid && TrySerialize(ToDto(proof!), out json, ref validation);
    }

    /// <summary>Parses exact canonical authority-proof JSON without treating it as verified.</summary>
    public static bool TryDeserializeProof(string? json, out CredentialAuthorityProof? proof, out CredentialContractValidationResult validation)
    {
        proof = null;
        if (!TryDeserialize<ProofDto>(json, out var dto, out validation) || !TryBuild(dto!, out proof, out validation) || !TrySerialize(proof, out var canonical, out validation))
        {
            return false;
        }

        return RequireCanonical(json!, canonical!, ref proof, out validation);
    }

    /// <summary>Serializes the deterministic proof claim covered by an issuer authenticator.</summary>
    /// <remarks>The authenticator is intentionally excluded to avoid a circular signing representation.</remarks>
    public static bool TrySerializeAuthorityClaim(CredentialAuthorityProof? proof, out string? json, out CredentialContractValidationResult validation)
    {
        json = null;
        validation = CredentialContractValidator.Validate(proof);
        if (!validation.IsValid)
        {
            return false;
        }

        var value = proof!;
        var claim = new ProofClaimDto(value.SchemaVersion, value.ProofId.Value, value.ReferenceId.Value, value.BindingHash.Value, ToDto(value.GrantedScope), value.ActorId, value.RunId.Value, value.AuthorityRevision, Time(value.IssuedAtUtc), Time(value.ExpiresAtUtc), value.IssuerId.Value);
        return TrySerialize(claim, out json, ref validation);
    }

    /// <summary>Serializes validated value-free use evidence.</summary>
    public static bool TrySerialize(CredentialUseEvidence? evidence, out string? json, out CredentialContractValidationResult validation)
    {
        json = null;
        validation = CredentialContractValidator.Validate(evidence);
        return validation.IsValid && TrySerialize(ToDto(evidence!), out json, ref validation);
    }

    /// <summary>Parses exact canonical value-free use-evidence JSON.</summary>
    public static bool TryDeserializeEvidence(string? json, out CredentialUseEvidence? evidence, out CredentialContractValidationResult validation)
    {
        evidence = null;
        if (!TryDeserialize<EvidenceDto>(json, out var dto, out validation) || !TryBuild(dto!, out evidence, out validation) || !TrySerialize(evidence, out var canonical, out validation))
        {
            return false;
        }

        return RequireCanonical(json!, canonical!, ref evidence, out validation);
    }

    /// <summary>Hashes a validated exact binding's canonical JSON.</summary>
    public static bool TryHash(CredentialCapabilityBinding? binding, out CredentialContractHash? hash, out CredentialContractValidationResult validation)
    {
        if (!TrySerialize(binding, out var json, out validation))
        {
            hash = null;
            return false;
        }

        hash = CredentialContractHash.Compute(json!);
        return true;
    }

    /// <summary>Hashes a validated scope's canonical JSON.</summary>
    public static bool TryHash(CredentialScope? scope, out CredentialContractHash? hash, out CredentialContractValidationResult validation)
    {
        if (!TrySerialize(scope, out var json, out validation))
        {
            hash = null;
            return false;
        }

        hash = CredentialContractHash.Compute(json!);
        return true;
    }

    /// <summary>Hashes a validated public reference's canonical JSON.</summary>
    public static bool TryHash(CredentialReference? reference, out CredentialContractHash? hash, out CredentialContractValidationResult validation)
    {
        if (!TrySerialize(reference, out var json, out validation))
        {
            hash = null;
            return false;
        }

        hash = CredentialContractHash.Compute(json!);
        return true;
    }

    /// <summary>Hashes a validated authority proof's canonical public JSON.</summary>
    public static bool TryHash(CredentialAuthorityProof? proof, out CredentialContractHash? hash, out CredentialContractValidationResult validation)
    {
        if (!TrySerialize(proof, out var json, out validation))
        {
            hash = null;
            return false;
        }

        hash = CredentialContractHash.Compute(json!);
        return true;
    }

    /// <summary>Hashes validated value-free use evidence's canonical JSON.</summary>
    public static bool TryHash(CredentialUseEvidence? evidence, out CredentialContractHash? hash, out CredentialContractValidationResult validation)
    {
        if (!TrySerialize(evidence, out var json, out validation))
        {
            hash = null;
            return false;
        }

        hash = CredentialContractHash.Compute(json!);
        return true;
    }

    private static bool TrySerialize<T>(T dto, out string? json, ref CredentialContractValidationResult validation)
    {
        json = JsonSerializer.Serialize(dto, _options);
        if (json.Length <= CredentialContractLimits.MaxCanonicalJsonCharacters)
        {
            return true;
        }

        json = null;
        validation = Invalid(CredentialContractErrorCode.CredentialContractTooLarge);
        return false;
    }

    private static bool TryDeserialize<T>(string? json, out T? dto, out CredentialContractValidationResult validation)
    {
        dto = default;
        if (string.IsNullOrEmpty(json) || json.Length > CredentialContractLimits.MaxCanonicalJsonCharacters)
        {
            validation = Invalid(CredentialContractErrorCode.InvalidCredentialJson);
            return false;
        }

        try
        {
            dto = JsonSerializer.Deserialize<T>(json, _options);
            if (dto is not null)
            {
                validation = CredentialContractValidationResult.Valid;
                return true;
            }
        }
        catch (JsonException)
        {
        }

        validation = Invalid(CredentialContractErrorCode.InvalidCredentialJson);
        return false;
    }

    private static bool RequireCanonical<T>(string input, string canonical, ref T? value, out CredentialContractValidationResult validation) where T : class
    {
        if (string.Equals(input, canonical, StringComparison.Ordinal))
        {
            validation = CredentialContractValidationResult.Valid;
            return true;
        }

        value = null;
        validation = Invalid(CredentialContractErrorCode.NoncanonicalCredentialJson);
        return false;
    }

    private static ReferenceDto ToDto(CredentialReference value)
    {
        return new ReferenceDto(value.SchemaVersion, value.Id.Value, value.Type, Lifecycle(value.Status), value.OwnerId, value.Purpose, value.ProviderId.Value, Time(value.CreatedAtUtc), Time(value.UpdatedAtUtc), value.ExpiresAtUtc is null ? null : Time(value.ExpiresAtUtc.Value), new SortedDictionary<string, string>(value.Metadata.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal), StringComparer.Ordinal));
    }

    private static ScopeDto ToDto(CredentialScope value)
    {
        var capability = value.Capability is null ? null : new CapabilityDto(value.Capability.Id.Value, value.Capability.Version.Value, value.Capability.Hash.Value, value.Implementation!.ProviderId.Value, value.Implementation.ImplementationId);
        return new ScopeDto(value.WorkspaceId, value.RoleId, value.LoopId, value.LoopRevision, value.NodeId, capability, value.Service, value.Target, value.OperationClass, value.ActorId, value.NotBeforeUtc is null ? null : Time(value.NotBeforeUtc.Value), value.NotAfterUtc is null ? null : Time(value.NotAfterUtc.Value));
    }

    private static BindingDto ToDto(CredentialCapabilityBinding value)
    {
        var capability = new CapabilityDto(value.Capability.Id.Value, value.Capability.Version.Value, value.Capability.Hash.Value, value.Implementation.ProviderId.Value, value.Implementation.ImplementationId);
        return new BindingDto(value.SchemaVersion, value.ReferenceId.Value, value.Requirement.Name, capability, ToDto(value.Scope));
    }

    private static ProofDto ToDto(CredentialAuthorityProof value)
    {
        return new ProofDto(value.SchemaVersion, value.ProofId.Value, value.ReferenceId.Value, value.BindingHash.Value, ToDto(value.GrantedScope), value.ActorId, value.RunId.Value, value.AuthorityRevision, Time(value.IssuedAtUtc), Time(value.ExpiresAtUtc), value.IssuerId.Value, value.Authenticator.Value);
    }

    private static EvidenceDto ToDto(CredentialUseEvidence value)
    {
        return new EvidenceDto(value.SchemaVersion, value.EvidenceId.Value, value.ReferenceId.Value, value.BindingHash.Value, value.ProofId.Value, value.RunId.Value, ToDto(value.UsedScope), Time(value.UsedAtUtc), Outcome(value.Outcome), value.RedactionApplied);
    }

    private static bool TryBuild(ReferenceDto dto, out CredentialReference? value, out CredentialContractValidationResult validation)
    {
        value = null;
        if (!CredentialReferenceId.TryParse(dto.Id, out var id, out _) || !CredentialProviderId.TryParse(dto.ProviderId, out var provider, out _) || !TryLifecycle(dto.Status, out var status) || !TryTime(dto.CreatedAtUtc, out var created) || !TryTime(dto.UpdatedAtUtc, out var updated) || dto.ExpiresAtUtc is not null && !TryTime(dto.ExpiresAtUtc, out _))
        {
            validation = Invalid(CredentialContractErrorCode.InvalidCredentialReference);
            return false;
        }

        DateTimeOffset? expires = dto.ExpiresAtUtc is null ? null : DateTimeOffset.ParseExact(dto.ExpiresAtUtc, "O", null);
        value = new CredentialReference(dto.SchemaVersion, id!, dto.Type, status, dto.OwnerId, dto.Purpose, provider!, created, updated, expires, dto.Metadata);
        validation = CredentialContractValidator.Validate(value);
        return validation.IsValid;
    }

    private static bool TryBuild(ScopeDto? dto, out CredentialScope? value, out CredentialContractValidationResult validation)
    {
        value = null;
        if (dto is null)
        {
            validation = Invalid(CredentialContractErrorCode.InvalidCredentialScope);
            return false;
        }

        CapabilityDescriptorIdentity? identity = null;
        CapabilityImplementationIdentity? implementation = null;
        if (dto.Capability is not null && !TryBuild(dto.Capability, out identity, out implementation))
        {
            validation = Invalid(CredentialContractErrorCode.InvalidCapabilityIdentity);
            return false;
        }

        if (dto.NotBeforeUtc is not null && !TryTime(dto.NotBeforeUtc, out _) || dto.NotAfterUtc is not null && !TryTime(dto.NotAfterUtc, out _))
        {
            validation = Invalid(CredentialContractErrorCode.InvalidTimestamp);
            return false;
        }

        DateTimeOffset? notBefore = dto.NotBeforeUtc is null ? null : DateTimeOffset.ParseExact(dto.NotBeforeUtc, "O", null);
        DateTimeOffset? notAfter = dto.NotAfterUtc is null ? null : DateTimeOffset.ParseExact(dto.NotAfterUtc, "O", null);
        value = new CredentialScope(dto.WorkspaceId, dto.RoleId, dto.LoopId, dto.LoopRevision, dto.NodeId, identity, implementation, dto.Service, dto.Target, dto.OperationClass, dto.ActorId, notBefore, notAfter);
        validation = CredentialContractValidator.Validate(value);
        return validation.IsValid;
    }

    private static bool TryBuild(BindingDto dto, out CredentialCapabilityBinding? value, out CredentialContractValidationResult validation)
    {
        value = null;
        if (!CredentialReferenceId.TryParse(dto.ReferenceId, out var referenceId, out _) || !CapabilitySecretRequirement.TryParse(dto.Requirement, out var requirement, out _) || !TryBuild(dto.Capability, out var identity, out var implementation) || !TryBuild(dto.Scope, out var scope, out _))
        {
            validation = Invalid(CredentialContractErrorCode.InvalidCredentialBinding);
            return false;
        }

        value = new CredentialCapabilityBinding(dto.SchemaVersion, referenceId!, requirement!, identity!, implementation!, scope!);
        validation = CredentialContractValidator.Validate(value);
        return validation.IsValid;
    }

    private static bool TryBuild(ProofDto dto, out CredentialAuthorityProof? value, out CredentialContractValidationResult validation)
    {
        value = null;
        if (!CredentialContractId.TryParse(dto.ProofId, out var proofId, out _) || !CredentialReferenceId.TryParse(dto.ReferenceId, out var referenceId, out _) || !CredentialContractHash.TryParse(dto.BindingHash, out var bindingHash, out _) || !TryBuild(dto.GrantedScope, out var scope, out _) || !CredentialContractId.TryParse(dto.RunId, out var runId, out _) || !TryTime(dto.IssuedAtUtc, out var issued) || !TryTime(dto.ExpiresAtUtc, out var expires) || !CredentialProviderId.TryParse(dto.IssuerId, out var issuer, out _) || !CredentialContractHash.TryParse(dto.Authenticator, out var authenticator, out _))
        {
            validation = Invalid(CredentialContractErrorCode.InvalidCredentialAuthorityProof);
            return false;
        }

        value = new CredentialAuthorityProof(dto.SchemaVersion, proofId!, referenceId!, bindingHash!, scope!, dto.ActorId, runId!, dto.AuthorityRevision, issued, expires, issuer!, authenticator!);
        validation = CredentialContractValidator.Validate(value);
        return validation.IsValid;
    }

    private static bool TryBuild(EvidenceDto dto, out CredentialUseEvidence? value, out CredentialContractValidationResult validation)
    {
        value = null;
        if (!CredentialContractId.TryParse(dto.EvidenceId, out var evidenceId, out _) || !CredentialReferenceId.TryParse(dto.ReferenceId, out var referenceId, out _) || !CredentialContractHash.TryParse(dto.BindingHash, out var bindingHash, out _) || !CredentialContractId.TryParse(dto.ProofId, out var proofId, out _) || !CredentialContractId.TryParse(dto.RunId, out var runId, out _) || !TryBuild(dto.UsedScope, out var scope, out _) || !TryTime(dto.UsedAtUtc, out var usedAt) || !TryOutcome(dto.Outcome, out var outcome))
        {
            validation = Invalid(CredentialContractErrorCode.InvalidCredentialUseEvidence);
            return false;
        }

        value = new CredentialUseEvidence(dto.SchemaVersion, evidenceId!, referenceId!, bindingHash!, proofId!, runId!, scope!, usedAt, outcome, dto.RedactionApplied);
        validation = CredentialContractValidator.Validate(value);
        return validation.IsValid;
    }

    private static bool TryBuild(CapabilityDto? dto, out CapabilityDescriptorIdentity? identity, out CapabilityImplementationIdentity? implementation)
    {
        identity = null;
        implementation = null;
        if (dto is null || !CapabilityId.TryParse(dto.Id, out var id, out _) || !CapabilityVersion.TryParse(dto.Version, out var version, out _) || !CapabilityDescriptorHash.TryParse(dto.DescriptorHash, out var hash, out _) || !CapabilityProviderId.TryParse(dto.ProviderId, out var provider, out _) || !CapabilityIdentifierRules.IsPath(dto.ImplementationId, CapabilityContractLimits.MaxImplementationIdCharacters))
        {
            return false;
        }

        identity = new CapabilityDescriptorIdentity(id!, version!, hash!);
        implementation = new CapabilityImplementationIdentity(provider!, dto.ImplementationId);
        return true;
    }

    private static string Time(DateTimeOffset value) => value.ToUniversalTime().ToString("O");
    private static bool TryTime(string? value, out DateTimeOffset parsed) => DateTimeOffset.TryParseExact(value, "O", null, System.Globalization.DateTimeStyles.None, out parsed) && parsed.Offset == TimeSpan.Zero && string.Equals(value, Time(parsed), StringComparison.Ordinal);

    private static string Lifecycle(CredentialLifecycleStatus status) => status switch { CredentialLifecycleStatus.Active => "active", CredentialLifecycleStatus.Disabled => "disabled", CredentialLifecycleStatus.Expired => "expired", CredentialLifecycleStatus.Revoked => "revoked", _ => throw new ArgumentOutOfRangeException(nameof(status)) };
    private static bool TryLifecycle(string value, out CredentialLifecycleStatus status) { status = value switch { "active" => CredentialLifecycleStatus.Active, "disabled" => CredentialLifecycleStatus.Disabled, "expired" => CredentialLifecycleStatus.Expired, "revoked" => CredentialLifecycleStatus.Revoked, _ => (CredentialLifecycleStatus)(-1) }; return Enum.IsDefined(status); }
    private static string Outcome(CredentialUseOutcome outcome) => outcome switch { CredentialUseOutcome.Succeeded => "succeeded", CredentialUseOutcome.FailedBeforeActuation => "failed-before-actuation", CredentialUseOutcome.FailedAfterActuation => "failed-after-actuation", CredentialUseOutcome.OutcomeUncertain => "outcome-uncertain", _ => throw new ArgumentOutOfRangeException(nameof(outcome)) };
    private static bool TryOutcome(string value, out CredentialUseOutcome outcome) { outcome = value switch { "succeeded" => CredentialUseOutcome.Succeeded, "failed-before-actuation" => CredentialUseOutcome.FailedBeforeActuation, "failed-after-actuation" => CredentialUseOutcome.FailedAfterActuation, "outcome-uncertain" => CredentialUseOutcome.OutcomeUncertain, _ => (CredentialUseOutcome)(-1) }; return Enum.IsDefined(outcome); }

    private static CredentialContractValidationResult Invalid(CredentialContractErrorCode code) => CredentialContractValidationResult.Rejected(code);

    private sealed record ReferenceDto(int SchemaVersion, string Id, string Type, string Status, string OwnerId, string Purpose, string ProviderId, string CreatedAtUtc, string UpdatedAtUtc, string? ExpiresAtUtc, SortedDictionary<string, string> Metadata);
    private sealed record CapabilityDto(string Id, string Version, string DescriptorHash, string ProviderId, string ImplementationId);
    private sealed record ScopeDto(string? WorkspaceId, string? RoleId, string? LoopId, long? LoopRevision, string? NodeId, CapabilityDto? Capability, string? Service, string? Target, string? OperationClass, string? ActorId, string? NotBeforeUtc, string? NotAfterUtc);
    private sealed record BindingDto(int SchemaVersion, string ReferenceId, string Requirement, CapabilityDto Capability, ScopeDto Scope);
    private sealed record ProofDto(int SchemaVersion, string ProofId, string ReferenceId, string BindingHash, ScopeDto GrantedScope, string ActorId, string RunId, long AuthorityRevision, string IssuedAtUtc, string ExpiresAtUtc, string IssuerId, string Authenticator);
    private sealed record ProofClaimDto(int SchemaVersion, string ProofId, string ReferenceId, string BindingHash, ScopeDto GrantedScope, string ActorId, string RunId, long AuthorityRevision, string IssuedAtUtc, string ExpiresAtUtc, string IssuerId);
    private sealed record EvidenceDto(int SchemaVersion, string EvidenceId, string ReferenceId, string BindingHash, string ProofId, string RunId, ScopeDto UsedScope, string UsedAtUtc, string Outcome, bool RedactionApplied);
}
