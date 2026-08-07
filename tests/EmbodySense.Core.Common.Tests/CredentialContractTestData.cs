using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Credentials;
using EmbodySense.Core.Common.Credentials.Models;

namespace EmbodySense.Core.Common.Tests;

internal static class CredentialContractTestData
{
    internal static readonly DateTimeOffset Now = new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

    internal static CredentialReference Reference(IReadOnlyDictionary<string, string>? metadata = null)
    {
        return new CredentialReference(1, ReferenceId("credential-1"), "api-token", CredentialLifecycleStatus.Active, "user-1", "Call the example service.", ProviderId("org.embodysense.windows"), Now.AddDays(-2), Now.AddDays(-1), Now.AddDays(30), metadata ?? new Dictionary<string, string> { ["service"] = "Example", ["display-name"] = "Example token" });
    }

    internal static CredentialScope Scope(string workspace = "workspace-1", string? role = "role-1", string? loop = "loop-1", long? revision = 4, string? node = "node-1", string? service = "example-api", string? target = "api.example.com", string? operation = "read", string? actor = "user-1", DateTimeOffset? notBefore = null, DateTimeOffset? notAfter = null)
    {
        var descriptor = CapabilityContractTestData.ValidDescriptor();
        Assert.True(CapabilityDescriptorIdentity.TryCreate(descriptor, out var identity, out var validation), string.Join(';', validation.Errors.Select(error => error.Message)));
        return new CredentialScope(workspace, role, loop, revision, node, identity, descriptor.Implementation, service, target, operation, actor, notBefore ?? Now.AddHours(-1), notAfter ?? Now.AddHours(1));
    }

    internal static CredentialCapabilityBinding Binding(CredentialScope? scope = null)
    {
        var selected = scope ?? Scope();
        return new CredentialCapabilityBinding(1, ReferenceId("credential-1"), CapabilityContractTestData.Secret("provider-token"), selected.Capability!, selected.Implementation!, selected);
    }

    internal static CredentialAuthorityProof Proof(CredentialCapabilityBinding binding, CredentialScope? scope = null)
    {
        Assert.True(CredentialContractJson.TryHash(binding, out var hash, out var validation), string.Join(';', validation.Errors.Select(error => error.Message)));
        return new CredentialAuthorityProof(1, ContractId("proof-1"), binding.ReferenceId, hash!, scope ?? binding.Scope, "user-1", ContractId("run-1"), 7, Now.AddMinutes(-5), Now.AddMinutes(5), ProviderId("org.embodysense.authority"), CredentialContractHash.Compute("test-authenticator"));
    }

    internal static CredentialUseEvidence Evidence(CredentialCapabilityBinding binding)
    {
        var proof = Proof(binding);
        return new CredentialUseEvidence(1, ContractId("evidence-1"), binding.ReferenceId, proof.BindingHash, proof.ProofId, proof.RunId, binding.Scope, Now, CredentialUseOutcome.Succeeded, true);
    }

    internal static CredentialReferenceId ReferenceId(string value)
    {
        Assert.True(CredentialReferenceId.TryParse(value, out var parsed, out var error), error?.Message);
        return parsed!;
    }

    internal static CredentialProviderId ProviderId(string value)
    {
        Assert.True(CredentialProviderId.TryParse(value, out var parsed, out var error), error?.Message);
        return parsed!;
    }

    internal static CredentialContractId ContractId(string value)
    {
        Assert.True(CredentialContractId.TryParse(value, out var parsed, out var error), error?.Message);
        return parsed!;
    }
}
