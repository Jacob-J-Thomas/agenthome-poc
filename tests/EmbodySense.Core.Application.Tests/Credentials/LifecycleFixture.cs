using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Application.Credentials;
using EmbodySense.Core.Application.Credentials.Models;
using EmbodySense.Core.Application.Governance.Audit;
using EmbodySense.Core.Application.Tests.Capabilities;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Credentials;
using EmbodySense.Core.Common.Credentials.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.Credentials;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Application.Tests.Credentials;

internal sealed class LifecycleFixture : IDisposable
{
    private static readonly DateTimeOffset _timestamp = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
    private readonly TestWorkspace _workspace = new();
    private readonly FileCapabilityCatalogTrustProvider _trustProvider;

    internal LifecycleFixture()
    {
        ActorId = Environment.UserName;
        var manifest = CapabilityArtifactTestData.Manifest(secrets: true);
        Assert.True(CapabilityDescriptorIdentity.TryCreate(manifest.Descriptor, out var identity, out _));
        Assert.True(CapabilitySecretRequirement.TryParse("api_token", out var requirement, out _));
        Assert.True(CredentialReferenceId.TryParse("credential-1", out var referenceId, out _));
        Assert.True(CredentialProviderId.TryParse(manifest.Descriptor.Implementation.ProviderId.Value, out var providerId, out _));
        Reference = new CredentialReference(1, referenceId!, "api-token", CredentialLifecycleStatus.Active, ActorId, "Test credential lifecycle.", providerId!, _timestamp, _timestamp, null, new Dictionary<string, string> { ["service"] = "example" });
        var scope = new CredentialScope("workspace-1", "role-1", "loop-1", 7, "node-1", identity, manifest.Descriptor.Implementation, "example", "target", "read", ActorId, null, null);
        Binding = new CredentialCapabilityBinding(1, Reference.Id, requirement!, identity!, manifest.Descriptor.Implementation, scope);
        Provider = new SecureFakeProvider();
        Dependents = new StubCapabilityDependentIndex { Snapshot = new CapabilityDependentIndexSnapshot(CapabilityDependentIndexStatus.Available, Hash('a'), [], "available") };
        ActiveRuns = new StubCredentialActiveRunIndex();
        LocatorSource = new StubCredentialProviderLocatorSource();
        Audit = new RecordingCapabilityAuditLog();
        var paths = new WorkspacePaths(_workspace.RootPath);
        _trustProvider = new FileCapabilityCatalogTrustProvider(_workspace.ServerStatePath);
        Registry = new CredentialLifecycleRegistryProbe(paths, new CredentialRegistryStore(paths, _trustProvider, LocatorSource, new FixedTimeProvider(_timestamp)));
        Service = CredentialLifecyclePersistenceFactory.Create(paths, _trustProvider, LocatorSource, Provider, LocatorSource, Dependents, ActiveRuns, Audit, new FixedTimeProvider(_timestamp));
    }

    internal string ActorId { get; }
    internal CredentialReference Reference { get; }
    internal CredentialCapabilityBinding Binding { get; }
    internal CredentialLifecycleRegistryProbe Registry { get; }
    internal SecureFakeProvider Provider { get; }
    internal StubCapabilityDependentIndex Dependents { get; }
    internal StubCredentialActiveRunIndex ActiveRuns { get; }
    internal StubCredentialProviderLocatorSource LocatorSource { get; }
    internal RecordingCapabilityAuditLog Audit { get; }
    internal CredentialLifecycleService Service { get; }
    internal IReadOnlyList<CredentialProviderHealthStatus?> MutationHealth => Registry.Mutations.Select(mutation => mutation.ResultHealth).Where(health => health is not null).ToArray();

    internal CredentialLifecycleRequest CreateRequest(string operationId, int length) => new(CredentialLifecycleOperationKind.Create, Id(operationId), Reference.Id, "workspace-1", ActorId, 0, _timestamp, length, Reference, Binding, Id("consent-ungranted"));
    internal CredentialLifecyclePreviewRequest PreviewRequest(string operationId, CredentialLifecycleOperationKind kind, long revision) => new(Id(operationId), kind, Reference.Id, "workspace-1", ActorId, revision);
    internal CredentialLifecycleRequest DestructiveRequest(string operationId, CredentialLifecycleOperationKind kind, long revision, CredentialLifecyclePreview preview, int length = 0) => new(kind, Id(operationId), Reference.Id, "workspace-1", ActorId, revision, _timestamp, length, Preview: preview, Confirmed: true);
    internal CredentialLifecyclePreviewRequest ReconciliationPreviewRequest(string operationId, string interruptedRepairOperationId, long revision) => new(Id(operationId), CredentialLifecycleOperationKind.ReconcileRepair, Reference.Id, "workspace-1", ActorId, revision, Id(interruptedRepairOperationId));
    internal CredentialLifecycleRequest ReconciliationRequest(string operationId, string interruptedRepairOperationId, long revision, CredentialLifecyclePreview preview) => new(CredentialLifecycleOperationKind.ReconcileRepair, Id(operationId), Reference.Id, "workspace-1", ActorId, revision, _timestamp, Preview: preview, Confirmed: true, InterruptedRepairOperationId: Id(interruptedRepairOperationId));
    internal CredentialLifecycleService CreateService(IAuditLog auditLog)
    {
        var paths = new WorkspacePaths(_workspace.RootPath);
        return CredentialLifecyclePersistenceFactory.Create(paths, _trustProvider, LocatorSource, Provider, LocatorSource, Dependents, ActiveRuns, auditLog, new FixedTimeProvider(_timestamp));
    }

    public void Dispose() => _workspace.Dispose();

    private static string Hash(char value) => "sha256:" + new string(value, 64);

    private static CredentialContractId Id(string value)
    {
        Assert.True(CredentialContractId.TryParse(value, out var id, out _));
        return id!;
    }
}
