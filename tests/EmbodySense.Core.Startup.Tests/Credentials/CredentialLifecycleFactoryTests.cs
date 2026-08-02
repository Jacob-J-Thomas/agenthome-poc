using EmbodySense.Core.Application.Credentials;
using EmbodySense.Core.Application.Credentials.Models;
using EmbodySense.Core.Common.Credentials;
using EmbodySense.Core.Common.Credentials.Models;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Audit;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.Credentials;
using EmbodySense.Core.Startup.Credentials;
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
        var interruptedRepairId = Id("factory-interrupted-repair");
        var forged = await bootstrap.MutateAsync(new CredentialRegistryMutation(CredentialRegistryMutationKind.BeginRepair, interruptedRepairId, 0, ReferenceId(), null, null, null, null, null, LifecycleOperation: (int)CredentialLifecycleOperationKind.Repair, ActorId: Environment.UserName, PreviewHash: Hash('b'), LifecycleRequestHash: Hash('c'), LifecyclePhase: CredentialLifecycleMutationPhase.Intent, LifecycleIntentOperationId: interruptedRepairId, WorkspaceId: "workspace-1", LifecycleAudit: IntentAuditPayload()));
        Assert.Equal(CredentialRegistryMutationStatus.Invalid, forged.Status);
        Assert.Equal(CredentialFailureCode.Unauthorized, forged.Failure!.Code);
        var service = CredentialLifecycleFactory.Create(paths, trust, adapter, adapter, adapter, adapter, adapter, new AuditLog(paths));
        var previewRequest = new CredentialLifecyclePreviewRequest(Id("factory-reconcile"), CredentialLifecycleOperationKind.ReconcileRepair, ReferenceId(), "workspace-1", Environment.UserName, 0, interruptedRepairId);

        var preview = await service.PreviewAsync(previewRequest);
        Assert.Equal(CredentialLifecyclePreviewStatus.Conflict, preview.Status);
        var request = new CredentialLifecycleRequest(CredentialLifecycleOperationKind.ReconcileRepair, Id("factory-reconcile"), ReferenceId(), "workspace-1", Environment.UserName, 0, _timestamp, Preview: preview, Confirmed: true, InterruptedRepairOperationId: interruptedRepairId);

        var result = await service.ExecuteAsync(request);

        Assert.Equal(CredentialLifecycleResultStatus.Conflict, result.Status);
        Assert.Equal(0, adapter.DeleteCount);
        var read = await bootstrap.ReadAsync();
        Assert.Equal(0, read.RegistryRevision);
        Assert.DoesNotContain(read.Operations, operation => operation.OperationId.Equals(interruptedRepairId) || operation.OperationId.Equals(request.OperationId));
    }

    private static CredentialReferenceId ReferenceId() => CredentialReferenceId.TryParse("credential-factory", out var parsed, out _) ? parsed! : throw new InvalidOperationException();
    private static CredentialContractId Id(string value) => CredentialContractId.TryParse(value, out var parsed, out _) ? parsed! : throw new InvalidOperationException();
    private static string Hash(char value) => "sha256:" + new string(value, 64);
    private static CredentialLifecycleAuditPayload IntentAuditPayload() => new(AuditSchema.Actions.CredentialLifecycleIntent, AuditSchema.Outcomes.Started, "Credential lifecycle intent durably recorded.");
}
