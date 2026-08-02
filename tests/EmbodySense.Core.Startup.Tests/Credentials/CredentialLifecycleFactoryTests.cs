using EmbodySense.Core.Application.Credentials;
using EmbodySense.Core.Application.Credentials.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Credentials;
using EmbodySense.Core.Common.Credentials.Models;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Audit;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.Credentials;
using EmbodySense.Core.Startup.Credentials;
using EmbodySense.Core.Startup.Tests.Capabilities;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Startup.Tests.Credentials;

public sealed class CredentialLifecycleFactoryTests
{
    private static readonly DateTimeOffset _timestamp = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FactoryComposesWithoutMutationAndRejectsMissingRequiredPorts()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new FileCapabilityCatalogTrustProvider(workspace.ServerStatePath);
        var adapter = new CredentialLifecycleFactoryTestAdapter();

        var service = CredentialLifecycleFactory.Create(paths, trust, adapter, adapter, adapter, adapter, adapter, new AuditLog(paths));

        Assert.NotNull(service);
        Assert.False(File.Exists(paths.CredentialRegistryDocumentPath));
        Assert.False(Directory.Exists(workspace.ServerStatePath));
        Assert.Throws<ArgumentNullException>(() => CredentialLifecycleFactory.Create(paths, trust, adapter, adapter, adapter, adapter, null!, new AuditLog(paths)));
    }

    [Fact]
    public async Task FactoryCannotCompleteReconciliationFromRawForgedRepairEvidence()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new FileCapabilityCatalogTrustProvider(workspace.ServerStatePath);
        var adapter = new CredentialLifecycleFactoryTestAdapter();
        var bootstrap = new CredentialRegistryStore(paths, trust, adapter);
        var interruptedRepairId = await SeedPreparedStateAndRejectForgedRepairAsync(bootstrap);
        var service = CredentialLifecycleFactory.Create(paths, trust, adapter, adapter, adapter, adapter, adapter, new AuditLog(paths));
        var previewRequest = new CredentialLifecyclePreviewRequest(Id("factory-reconcile"), CredentialLifecycleOperationKind.ReconcileRepair, ReferenceId(), "workspace-1", Environment.UserName, 2, interruptedRepairId);

        var preview = await service.PreviewAsync(previewRequest);
        Assert.Equal(CredentialLifecyclePreviewStatus.Conflict, preview.Status);
        var request = new CredentialLifecycleRequest(CredentialLifecycleOperationKind.ReconcileRepair, Id("factory-reconcile"), ReferenceId(), "workspace-1", Environment.UserName, 2, _timestamp, Preview: preview, Confirmed: true, InterruptedRepairOperationId: interruptedRepairId);

        var result = await service.ExecuteAsync(request);

        Assert.Equal(CredentialLifecycleResultStatus.Conflict, result.Status);
        Assert.Equal(0, adapter.DeleteCount);
        var read = await bootstrap.ReadAsync();
        Assert.Equal(2, read.RegistryRevision);
        Assert.DoesNotContain(read.Operations, operation => operation.OperationId.Equals(interruptedRepairId) || operation.OperationId.Equals(request.OperationId));
    }

    private static async Task<CredentialContractId> SeedPreparedStateAndRejectForgedRepairAsync(CredentialRegistryStore store)
    {
        var reference = Reference();
        var binding = Binding();
        var createId = Id("factory-create");
        var createHash = Hash('a');
        var create = new CredentialRegistryMutation(CredentialRegistryMutationKind.BeginCreate, createId, 0, ReferenceId(), reference, binding, Id("factory-consent"), CredentialProviderHealthStatus.NeedsRepair, null, false, (int)CredentialLifecycleOperationKind.Create, Environment.UserName, null, createHash, CredentialLifecycleMutationPhase.Intent, createId, null, "workspace-1", IntentAuditPayload());
        Assert.Equal(CredentialRegistryMutationStatus.Applied, (await store.MutateAsync(create)).Status);
        var prepared = new CredentialRegistryMutation(CredentialRegistryMutationKind.Register, Id("factory-locator"), 1, ReferenceId(), reference, binding, Id("factory-consent"), CredentialProviderHealthStatus.NeedsRepair, Locator(), false, (int)CredentialLifecycleOperationKind.Create, Environment.UserName, null, createHash, CredentialLifecycleMutationPhase.LocatorPrepared, createId, null, "workspace-1");
        Assert.Equal(CredentialRegistryMutationStatus.Applied, (await store.MutateAsync(prepared)).Status);
        var repairId = Id("factory-interrupted-repair");
        var repair = new CredentialRegistryMutation(CredentialRegistryMutationKind.BeginRepair, repairId, 2, ReferenceId(), null, null, null, null, null, LifecycleOperation: (int)CredentialLifecycleOperationKind.Repair, ActorId: Environment.UserName, PreviewHash: Hash('b'), LifecycleRequestHash: Hash('c'), LifecyclePhase: CredentialLifecycleMutationPhase.Intent, LifecycleIntentOperationId: repairId, WorkspaceId: "workspace-1", LifecycleAudit: IntentAuditPayload());
        var forged = await store.MutateAsync(repair);
        Assert.Equal(CredentialRegistryMutationStatus.Invalid, forged.Status);
        Assert.Equal(CredentialFailureCode.Unauthorized, forged.Failure!.Code);
        return repairId;
    }

    private static CredentialReference Reference()
    {
        var binding = Binding();
        return new CredentialReference(1, ReferenceId(), "api-token", CredentialLifecycleStatus.Active, "user-1", "Exercise production credential composition.", ProviderId(binding.Implementation.ProviderId.Value), _timestamp, _timestamp, null, new Dictionary<string, string> { ["service"] = "Example" });
    }

    private static CredentialCapabilityBinding Binding()
    {
        var descriptor = CapabilityAdmissionLifecycleTestData.Stage().Manifest.Descriptor;
        Assert.True(CapabilityDescriptorIdentity.TryCreate(descriptor, out var identity, out var validation), string.Join(';', validation.Errors.Select(error => error.Message)));
        Assert.True(CapabilitySecretRequirement.TryParse("provider-token", out var requirement, out _));
        var scope = new CredentialScope("workspace-1", "role-1", "loop-1", 1, "node-1", identity, descriptor.Implementation, "example", "target", "read", "user-1", null, null);
        return new CredentialCapabilityBinding(1, ReferenceId(), requirement!, identity!, descriptor.Implementation, scope);
    }

    private static CredentialReferenceId ReferenceId() => CredentialReferenceId.TryParse("credential-factory", out var parsed, out _) ? parsed! : throw new InvalidOperationException();
    private static CredentialProviderId ProviderId(string value) => CredentialProviderId.TryParse(value, out var parsed, out _) ? parsed! : throw new InvalidOperationException();
    private static CredentialContractId Id(string value) => CredentialContractId.TryParse(value, out var parsed, out _) ? parsed! : throw new InvalidOperationException();
    private static CredentialProviderLocator Locator() => CredentialProviderLocator.TryParse("loc_0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", out var parsed) ? parsed! : throw new InvalidOperationException();
    private static string Hash(char value) => "sha256:" + new string(value, 64);
    private static CredentialLifecycleAuditPayload IntentAuditPayload() => new(AuditSchema.Actions.CredentialLifecycleIntent, AuditSchema.Outcomes.Started, "Credential lifecycle intent durably recorded.");
}
