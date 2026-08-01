using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Credentials;
using EmbodySense.Core.Common.Credentials.Models;

namespace EmbodySense.Core.Common.Tests;

public sealed class CredentialBindingProofEvidenceTests
{
    [Fact]
    public void Binding_reuses_exact_capability_identity_and_hashes_deterministically()
    {
        var binding = CredentialContractTestData.Binding();
        Assert.IsType<CapabilityDescriptorIdentity>(binding.Capability);
        Assert.IsType<CapabilityVersion>(binding.Capability.Version);
        Assert.IsType<CapabilityImplementationIdentity>(binding.Implementation);
        Assert.IsType<CapabilitySecretRequirement>(binding.Requirement);

        Assert.True(CredentialContractJson.TrySerialize(binding, out var json, out var validation), string.Join(';', validation.Errors.Select(error => error.Message)));
        Assert.True(CredentialContractJson.TryDeserializeBinding(json, out var parsed, out validation));
        Assert.True(CredentialContractJson.TrySerialize(parsed, out var roundTrip, out validation));
        Assert.Equal(json, roundTrip);
        Assert.True(CredentialContractJson.TryHash(binding, out var firstHash, out validation));
        Assert.True(CredentialContractJson.TryHash(parsed, out var secondHash, out validation));
        Assert.True(firstHash!.FixedTimeEquals(secondHash));
        Assert.True(CredentialContractJson.TryHash(CredentialContractTestData.Reference(), out _, out validation));
        Assert.True(CredentialContractJson.TryHash(binding.Scope, out _, out validation));
    }

    [Fact]
    public void Proof_and_evidence_round_trip_but_never_self_declare_authority()
    {
        var binding = CredentialContractTestData.Binding();
        var proof = CredentialContractTestData.Proof(binding);
        var evidence = CredentialContractTestData.Evidence(binding);

        Assert.True(CredentialContractJson.TrySerialize(proof, out var proofJson, out _));
        Assert.True(CredentialContractJson.TrySerializeAuthorityClaim(proof, out var claimJson, out _));
        Assert.True(CredentialContractJson.TryDeserializeProof(proofJson, out var parsedProof, out _));
        Assert.True(CredentialContractJson.TrySerialize(parsedProof, out var proofRoundTrip, out _));
        Assert.Equal(proofJson, proofRoundTrip);
        Assert.True(CredentialContractJson.TrySerialize(evidence, out var evidenceJson, out _));
        Assert.True(CredentialContractJson.TryDeserializeEvidence(evidenceJson, out var parsedEvidence, out _));
        Assert.True(CredentialContractJson.TrySerialize(parsedEvidence, out var evidenceRoundTrip, out _));
        Assert.Equal(evidenceJson, evidenceRoundTrip);
        Assert.DoesNotContain("trusted", proofJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("authorized", proofJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("authenticator", claimJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"redactionApplied\":true", evidenceJson, StringComparison.Ordinal);
        Assert.True(CredentialContractJson.TryHash(proof, out _, out _));
        Assert.True(CredentialContractJson.TryHash(evidence, out _, out _));
    }

    [Fact]
    public void Forged_binding_reference_scope_or_expired_proof_fails_use_validation()
    {
        var binding = CredentialContractTestData.Binding();
        Assert.True(CredentialContractJson.TryHash(binding, out var hash, out _));
        var proof = CredentialContractTestData.Proof(binding);
        var valid = new CredentialUseRequest(binding, hash!, binding.Scope, proof, CredentialContractTestData.Now);
        Assert.True(CredentialContractValidator.Validate(valid).IsValid);

        var forgedHash = valid with { BindingHash = CredentialContractHash.Compute("forged") };
        var forgedReference = valid with { AuthorityProof = proof with { ReferenceId = CredentialContractTestData.ReferenceId("credential-2") } };
        var widenedScope = valid with { RequestedScope = binding.Scope with { RoleId = null } };
        var expired = valid with { RequestedAtUtc = proof.ExpiresAtUtc };
        var longProof = proof with { ExpiresAtUtc = proof.IssuedAtUtc + CredentialContractLimits.MaxProofLifetime + TimeSpan.FromTicks(1) };

        Assert.Contains(CredentialContractValidator.Validate(forgedHash).Errors, error => error.Code == CredentialContractErrorCode.BindingHashMismatch);
        Assert.Contains(CredentialContractValidator.Validate(forgedReference).Errors, error => error.Code == CredentialContractErrorCode.ProofReferenceMismatch);
        Assert.Contains(CredentialContractValidator.Validate(widenedScope).Errors, error => error.Code == CredentialContractErrorCode.CredentialScopeMismatch);
        Assert.Contains(CredentialContractValidator.Validate(expired).Errors, error => error.Code == CredentialContractErrorCode.CredentialProofExpired);
        Assert.Contains(CredentialContractValidator.Validate(longProof).Errors, error => error.Code == CredentialContractErrorCode.InvalidProofLifetime);
    }

    [Fact]
    public void Request_time_must_be_inside_the_requested_narrowed_scope()
    {
        var broadScope = CredentialContractTestData.Scope(notBefore: CredentialContractTestData.Now.AddHours(-2), notAfter: CredentialContractTestData.Now.AddHours(2));
        var binding = CredentialContractTestData.Binding(broadScope);
        Assert.True(CredentialContractJson.TryHash(binding, out var bindingHash, out _));
        var proof = CredentialContractTestData.Proof(binding);
        var futureScope = broadScope with { NotBeforeUtc = CredentialContractTestData.Now.AddMinutes(30), NotAfterUtc = CredentialContractTestData.Now.AddHours(1) };
        var beforeWindow = new CredentialUseRequest(binding, bindingHash!, futureScope, proof, CredentialContractTestData.Now);
        var afterWindow = beforeWindow with { RequestedScope = broadScope with { NotBeforeUtc = CredentialContractTestData.Now.AddHours(-1), NotAfterUtc = CredentialContractTestData.Now }, RequestedAtUtc = CredentialContractTestData.Now };

        Assert.Contains(CredentialContractValidator.Validate(beforeWindow).Errors, error => error.Code == CredentialContractErrorCode.CredentialRequestedOutsideScope);
        Assert.Contains(CredentialContractValidator.Validate(afterWindow).Errors, error => error.Code == CredentialContractErrorCode.CredentialRequestedOutsideScope);
    }

    [Fact]
    public void Direct_contract_objects_with_null_nested_members_fail_closed_without_throwing()
    {
        var binding = CredentialContractTestData.Binding();
        var proof = CredentialContractTestData.Proof(binding);
        var evidence = CredentialContractTestData.Evidence(binding);
        Assert.True(CredentialContractJson.TryHash(binding, out var bindingHash, out _));
        var request = new CredentialUseRequest(binding, bindingHash!, binding.Scope, proof, CredentialContractTestData.Now);
        var malformed = new Func<CredentialContractValidationResult>[]
        {
            () => CredentialContractValidator.Validate(binding with { ReferenceId = null! }),
            () => CredentialContractValidator.Validate(binding with { Scope = null! }),
            () => CredentialContractValidator.Validate(binding with { Capability = null! }),
            () => CredentialContractValidator.Validate(proof with { ReferenceId = null! }),
            () => CredentialContractValidator.Validate(proof with { GrantedScope = null! }),
            () => CredentialContractValidator.Validate(evidence with { UsedScope = null! }),
            () => CredentialContractValidator.Validate(request with { Binding = null! }),
            () => CredentialContractValidator.Validate(request with { BindingHash = null! }),
            () => CredentialContractValidator.Validate(request with { RequestedScope = null! }),
            () => CredentialContractValidator.Validate(request with { AuthorityProof = null! })
        };

        foreach (var validate in malformed)
        {
            var exception = Record.Exception(() => validate());
            Assert.Null(exception);
            Assert.False(validate().IsValid);
        }

        Assert.False(CredentialContractJson.TrySerialize(binding with { Scope = null! }, out _, out _));
        Assert.False(CredentialContractJson.TrySerialize(proof with { GrantedScope = null! }, out _, out _));
        Assert.False(CredentialContractJson.TrySerialize(evidence with { UsedScope = null! }, out _, out _));
    }

    [Fact]
    public void Structured_errors_are_closed_bounded_and_value_free_across_projections()
    {
        const string Canary = "credential_canary_must_not_escape";
        var validation = CredentialContractValidationResult.Rejected(CredentialContractErrorCode.InvalidCredentialJson);
        var error = Assert.Single(validation.Errors);
        var serialized = System.Text.Json.JsonSerializer.Serialize(validation);
        var injectedValidation = CredentialContractValidator.Validate(CredentialContractTestData.Reference(new Dictionary<string, string> { [Canary] = "unsafe\u202e" }));
        var injectedProjection = System.Text.Json.JsonSerializer.Serialize(injectedValidation) + injectedValidation;
        var publicConstructors = typeof(CredentialContractError).GetMembers().Where(member => member.MemberType.ToString() == "Constructor").ToArray();
        var validationConstructors = typeof(CredentialContractValidationResult).GetMembers().Where(member => member.MemberType.ToString() == "Constructor").ToArray();

        Assert.Empty(publicConstructors);
        Assert.Empty(validationConstructors);
        Assert.IsType<CredentialContractErrorCode>(error.Code);
        Assert.Equal("invalid_credential_json", error.CanonicalCode);
        Assert.Equal("Credential contract rejected: invalid_credential_json.", error.Message);
        Assert.Equal("invalid_credential_json at $", error.ToString());
        Assert.DoesNotContain(Canary, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, validation.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, injectedProjection, StringComparison.Ordinal);
        Assert.True(error.Path.Length <= CredentialContractLimits.MaxErrorPathCharacters);
        Assert.True(validation.Errors.Count <= CredentialContractLimits.MaxValidationErrors);
    }

    [Fact]
    public void Public_and_persisted_credential_models_have_no_secret_envelope_key_or_private_locator_members()
    {
        var persistedTypes = new[]
        {
            typeof(CredentialReference),
            typeof(CredentialScope),
            typeof(CredentialCapabilityBinding),
            typeof(CredentialAuthorityProof),
            typeof(CredentialUseEvidence)
        };
        var forbidden = new[] { "SecretValue", "Plaintext", "Ciphertext", "EncryptedEnvelope", "KeyMaterial", "PrivateLocator", "ProviderLocator" };

        foreach (var type in persistedTypes)
        {
            var names = type.GetProperties().Select(property => property.Name).Concat(type.GetFields().Select(field => field.Name)).ToArray();
            Assert.DoesNotContain(names, name => forbidden.Any(token => name.Contains(token, StringComparison.OrdinalIgnoreCase)));
        }

        const string Canary = "credential-canary-never-serialize";
        var reference = CredentialContractTestData.Reference();
        var binding = CredentialContractTestData.Binding();
        var proof = CredentialContractTestData.Proof(binding);
        var evidence = CredentialContractTestData.Evidence(binding);
        Assert.True(CredentialContractJson.TrySerialize(reference, out var referenceJson, out _));
        Assert.True(CredentialContractJson.TrySerialize(binding, out var bindingJson, out _));
        Assert.True(CredentialContractJson.TrySerialize(proof, out var proofJson, out _));
        Assert.True(CredentialContractJson.TrySerialize(evidence, out var evidenceJson, out _));
        Assert.All(new[] { referenceJson, bindingJson, proofJson, evidenceJson }, json => Assert.DoesNotContain(Canary, json, StringComparison.Ordinal));
    }

    [Fact]
    public void Malformed_oversized_unsafe_and_noncanonical_contracts_fail_closed()
    {
        var binding = CredentialContractTestData.Binding();
        Assert.True(CredentialContractJson.TrySerialize(binding, out var json, out _));
        Assert.False(CredentialContractJson.TryDeserializeBinding(json!.Replace("\"workspace-1\"", "\"workspace\\u002d1\"", StringComparison.Ordinal), out _, out var noncanonical));
        Assert.Equal(CredentialContractErrorCode.NoncanonicalCredentialJson, Assert.Single(noncanonical.Errors).Code);
        Assert.False(CredentialContractJson.TryDeserializeBinding("{", out _, out _));
        Assert.False(CredentialContractJson.TryDeserializeBinding(new string('x', CredentialContractLimits.MaxCanonicalJsonCharacters + 1), out _, out _));
        Assert.False(CredentialContractJson.TryDeserializeBinding(json.Replace("workspace-1", "workspace\u202e", StringComparison.Ordinal), out _, out _));
        Assert.False(CredentialContractJson.TryDeserializeBinding(json.Replace("\"capability\":{", "\"capability\":null,\"discard\":{", StringComparison.Ordinal), out _, out _));
    }
}
