using EmbodySense.Core.Application.Credentials;
using EmbodySense.Core.Application.Credentials.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Credentials;
using EmbodySense.Core.Common.Credentials.Leases.Models;
using EmbodySense.Core.Common.Credentials.Models;

namespace EmbodySense.IntegrationTests.Architecture;

internal sealed class HostileCredentialRegistryStore : ICredentialRegistryStore
{
    private readonly CredentialRegistryReadResult _state;

    internal HostileCredentialRegistryStore()
    {
        var referenceId = ReferenceId("hostile-credential");
        var capability = new CapabilityDescriptorIdentity(CapabilityId("org.example/hostile"), CapabilityVersion("1.0.0"), CapabilityDescriptorHash("sha256:" + new string('a', 64)));
        var implementation = new CapabilityImplementationIdentity(CapabilityProviderId("org.example"), "hostile-provider");
        var binding = new CredentialCapabilityBinding(1, referenceId, Requirement("api_token"), capability, implementation, new CredentialScope("workspace-1", null, null, null, null, capability, implementation, "example", "target", "delete", Environment.UserName, null, null));
        Assert.True(CredentialContractJson.TryHash(binding, out var bindingHash, out _));
        var reference = new CredentialReference(1, referenceId, "api-token", CredentialLifecycleStatus.Active, Environment.UserName, "Hostile forged repair state.", CredentialProviderId("org.example"), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null, new Dictionary<string, string>());
        var repairOperationId = ContractId("hostile-repair-intent");
        var entry = new CredentialRegistryEntry(reference, binding, bindingHash!, ContractId("hostile-consent"), CredentialProviderHealthStatus.NeedsRepair, 2, ContractId("hostile-create"));
        var operation = new CredentialRegistryOperationEvidence(repairOperationId, ContractHash('b'), (int)CredentialRegistryMutationKind.BeginRepair, 3, referenceId, (int)CredentialLifecycleOperationKind.Repair, Environment.UserName, LifecycleRequestHash: "sha256:" + new string('c', 64), LifecyclePhase: CredentialLifecycleMutationPhase.Intent, LifecycleIntentOperationId: repairOperationId, WorkspaceId: "workspace-1");
        _state = new CredentialRegistryReadResult(3, [entry], [], [operation], [], null);
    }

    internal int MutationCount { get; private set; }

    public ValueTask<CredentialActorAuthentication> AuthenticateActorAsync(string actorId, CancellationToken cancellationToken) => ValueTask.FromResult(CredentialActorAuthentication.AuthenticatedUser);
    public ValueTask<CredentialReferenceLookupResult> GetAsync(CredentialReferenceId referenceId, CancellationToken cancellationToken) => ValueTask.FromResult(CredentialReferenceLookupResult.Found(_state.Entries[0].Reference));
    public Task<CredentialRegistryReadResult> ReadAsync(CancellationToken cancellationToken = default) => Task.FromResult(_state);

    public Task<CredentialRegistryMutationResult> MutateAsync(CredentialRegistryMutation mutation, CancellationToken cancellationToken = default)
    {
        MutationCount++;
        return Task.FromResult(new CredentialRegistryMutationResult(CredentialRegistryMutationStatus.Applied, mutation.OperationId, _state.RegistryRevision + 1, _state.Entries[0], null));
    }

    public Task<bool> AcknowledgeAuditAsync(CredentialContractId auditOperationId, CancellationToken cancellationToken = default) => Task.FromResult(true);
    public ValueTask<CredentialEvidenceWriteResult> ReserveAsync(CredentialLeaseIntent intent, CancellationToken cancellationToken) => ValueTask.FromResult(CredentialEvidenceWriteResult.Success());
    public ValueTask<CredentialEvidenceWriteResult> AppendAsync(CredentialUseEvidence evidence, CancellationToken cancellationToken) => ValueTask.FromResult(CredentialEvidenceWriteResult.Success());

    private static CapabilityId CapabilityId(string value) => EmbodySense.Core.Common.Capabilities.CapabilityId.TryParse(value, out var parsed, out _) ? parsed! : throw new InvalidOperationException();
    private static CapabilityVersion CapabilityVersion(string value) => EmbodySense.Core.Common.Capabilities.CapabilityVersion.TryParse(value, out var parsed, out _) ? parsed! : throw new InvalidOperationException();
    private static CapabilityDescriptorHash CapabilityDescriptorHash(string value) => EmbodySense.Core.Common.Capabilities.CapabilityDescriptorHash.TryParse(value, out var parsed, out _) ? parsed! : throw new InvalidOperationException();
    private static CapabilityProviderId CapabilityProviderId(string value) => EmbodySense.Core.Common.Capabilities.CapabilityProviderId.TryParse(value, out var parsed, out _) ? parsed! : throw new InvalidOperationException();
    private static CapabilitySecretRequirement Requirement(string value) => CapabilitySecretRequirement.TryParse(value, out var parsed, out _) ? parsed! : throw new InvalidOperationException();
    private static CredentialReferenceId ReferenceId(string value) => CredentialReferenceId.TryParse(value, out var parsed, out _) ? parsed! : throw new InvalidOperationException();
    private static CredentialProviderId CredentialProviderId(string value) => EmbodySense.Core.Common.Credentials.CredentialProviderId.TryParse(value, out var parsed, out _) ? parsed! : throw new InvalidOperationException();
    private static CredentialContractId ContractId(string value) => CredentialContractId.TryParse(value, out var parsed, out _) ? parsed! : throw new InvalidOperationException();
    private static CredentialContractHash ContractHash(char value) => CredentialContractHash.TryParse("sha256:" + new string(value, 64), out var parsed, out _) ? parsed! : throw new InvalidOperationException();
}
