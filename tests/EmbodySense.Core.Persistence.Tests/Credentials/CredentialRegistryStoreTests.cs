using EmbodySense.Core.Application.Credentials;
using EmbodySense.Core.Application.Credentials.Models;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Governance.Audit;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Credentials;
using EmbodySense.Core.Common.Credentials.Models;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Credentials;
using EmbodySense.Core.Persistence.Credentials.Models;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.Capabilities.Models;
using EmbodySense.Core.Persistence.Audit;
using EmbodySense.Core.Persistence.Tests.Capabilities;
using EmbodySense.Tests.Support;
using Microsoft.Win32.SafeHandles;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EmbodySense.Core.Persistence.Tests.Credentials;

public sealed class CredentialRegistryStoreTests
{
    [Fact]
    public async Task RawLifecycleAuthorityChangesAreDeniedAndRestartSafe()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var seeded = await SeedRegistrationAsync(paths);
        var store = Store(paths);
        var seededRevision = seeded.RegistryRevision!.Value;
        var rebound = Binding() with { Scope = Binding().Scope with { LoopRevision = 2 } };
        var bind = await store.MutateAsync(new CredentialRegistryMutation(CredentialRegistryMutationKind.Bind, Id("bind-1"), seededRevision, ReferenceId(), null, rebound, null, null, null, null, (int)CredentialLifecycleOperationKind.Bind, "user-1"));
        var consent = await store.MutateAsync(new CredentialRegistryMutation(CredentialRegistryMutationKind.Consent, Id("consent-2"), seededRevision, ReferenceId(), null, null, Id("consent-document-2"), null, null, true, (int)CredentialLifecycleOperationKind.Consent, "user-1"));
        var revokedReference = Reference() with { Status = CredentialLifecycleStatus.Revoked, UpdatedAtUtc = new DateTimeOffset(2026, 8, 1, 12, 1, 0, TimeSpan.Zero) };

        var revoked = await new CredentialRegistryStore(paths, TestTrust(paths), new RejectingCredentialProviderLocatorVerifier()).MutateAsync(new CredentialRegistryMutation(CredentialRegistryMutationKind.UpdatePosture, Id("revoke-1"), seededRevision, ReferenceId(), revokedReference, null, null, CredentialProviderHealthStatus.Revoked, null, null, (int)CredentialLifecycleOperationKind.Revoke, "user-1", "sha256:" + new string('b', 64), null, null, null, ["run-1", "run-2"]));

        Assert.All([bind, consent, revoked], result =>
        {
            Assert.Equal(CredentialRegistryMutationStatus.Invalid, result.Status);
            Assert.Equal(CredentialFailureCode.Unauthorized, result.Failure!.Code);
        });
        var entry = Assert.Single((await Store(paths).ReadAsync()).Entries);
        Assert.Equal(1, entry.Binding.Scope.LoopRevision);
        Assert.False(entry.ConsentGranted);
        Assert.Equal(CredentialLifecycleStatus.Active, entry.Reference.Status);
        Assert.Equal(CredentialProviderHealthStatus.Available, entry.Health);
        Assert.Equal(seededRevision, (await Store(paths).ReadAsync()).RegistryRevision);
        var publicDocument = await File.ReadAllTextAsync(paths.CredentialRegistryDocumentPath);
        Assert.Contains("\"lifecycleShape\": 1", publicDocument, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProviderCompletionPhaseCannotBeReservedWithoutExactDurableIntent()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = Store(paths);
        var intentId = Id("phase-intent");
        var completion = new CredentialRegistryMutation(CredentialRegistryMutationKind.SetHealth, Id("phase-complete"), 0, ReferenceId(), null, null, null, CredentialProviderHealthStatus.Available, null, LifecycleOperation: (int)CredentialLifecycleOperationKind.Create, ActorId: "user-1", LifecycleRequestHash: Hash('d'), LifecyclePhase: CredentialLifecycleMutationPhase.Complete, LifecycleIntentOperationId: intentId, WorkspaceId: "workspace-1", LifecycleAudit: AuditPayload("succeeded"));

        var result = await store.MutateAsync(completion);

        Assert.Equal(CredentialRegistryMutationStatus.Invalid, result.Status);
        Assert.Equal(CredentialFailureCode.Unauthorized, result.Failure!.Code);
    }

    [Theory]
    [InlineData(CredentialLifecycleMutationPhase.Complete, CredentialProviderHealthStatus.Available, "succeeded")]
    [InlineData(CredentialLifecycleMutationPhase.Rollback, CredentialProviderHealthStatus.Missing, "failed")]
    [InlineData(CredentialLifecycleMutationPhase.Uncertain, CredentialProviderHealthStatus.NeedsRepair, "failed")]
    public async Task CorrelatedProviderTerminalPhasesPersistExactOutboxAndProjection(CredentialLifecycleMutationPhase phase, CredentialProviderHealthStatus health, string auditOutcome)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var adapter = new CoordinatedCredentialCreateAdapter();
        var provider = new TerminalCreateCredentialValueProvider(phase);
        var service = CredentialLifecyclePersistenceFactory.Create(paths, TestTrust(paths), adapter, provider, adapter, new CapabilityDependentIndex([adapter]), adapter, new FailingAuditLog());
        var reference = Reference() with { OwnerId = Environment.UserName };
        var binding = Binding() with { Scope = Binding().Scope with { ActorId = Environment.UserName } };
        var request = new CredentialLifecycleRequest(CredentialLifecycleOperationKind.Create, Id($"matrix-{phase.ToString().ToLowerInvariant()}"), ReferenceId(), "workspace-1", Environment.UserName, 0, new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero), 4, reference, binding, Id("consent-matrix"));

        var result = await service.ExecuteAsync(request, destination =>
        {
            destination.Fill(1);
            return destination.Length;
        });
        var restarted = await Store(paths).ReadAsync();

        var expectedStatus = phase == CredentialLifecycleMutationPhase.Complete ? CredentialLifecycleResultStatus.Applied : phase == CredentialLifecycleMutationPhase.Rollback ? CredentialLifecycleResultStatus.Failed : CredentialLifecycleResultStatus.NeedsRepair;
        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(health, result.Health);
        Assert.Equal(health, Assert.Single(restarted.Entries).Health);
        Assert.Equal([CredentialLifecycleMutationPhase.Intent, CredentialLifecycleMutationPhase.LocatorPrepared, phase], restarted.Operations.Select(operation => operation.LifecyclePhase).ToArray());
        var terminal = Assert.Single(restarted.Operations, operation => operation.LifecyclePhase == phase);
        var pending = Assert.Single(restarted.PendingAudits, item => item.AuditOperationId.Equals(terminal.OperationId));
        Assert.Equal(AuditSchema.Actions.CredentialLifecycleOutcome, pending.Action);
        Assert.Equal(auditOutcome, pending.Outcome);
        Assert.Equal(1, provider.CreateCount);
    }

    [Fact]
    public async Task LocatorUncertaintyRemainsValueFreeAndCannotBeBypassedAcrossRestart()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var adapter = CredentialLifecyclePersistenceTestAdapter.Instance;
        var provider = new CountingCreateCredentialValueProvider();
        var service = CredentialLifecyclePersistenceFactory.Create(paths, TestTrust(paths), adapter, provider, adapter, new CapabilityDependentIndex([new StubCapabilityDependentIndexSource()]), adapter, new FailingAuditLog());
        var reference = Reference() with { OwnerId = Environment.UserName };
        var binding = Binding() with { Scope = Binding().Scope with { ActorId = Environment.UserName } };
        var request = new CredentialLifecycleRequest(CredentialLifecycleOperationKind.Create, Id("locator-uncertain-intent"), ReferenceId(), "workspace-1", Environment.UserName, 0, new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero), 4, reference, binding, Id("locator-uncertain-consent"));

        var result = await service.ExecuteAsync(request, destination =>
        {
            destination.Fill(1);
            return destination.Length;
        });

        Assert.Equal(CredentialLifecycleResultStatus.NeedsRepair, result.Status);
        Assert.Equal(0, provider.CreateCount);

        var restartedStore = Store(paths);
        var restarted = await restartedStore.ReadAsync();
        var bypass = await restartedStore.MutateAsync(Register(2));
        var competing = request with { OperationId = Id("locator-uncertain-competing"), ExpectedRegistryRevision = 2 };

        Assert.Empty(restarted.Entries);
        Assert.Empty(JsonNode.Parse(await File.ReadAllTextAsync(paths.CredentialRegistryPrivateDocumentPath))!["locators"]!.AsArray());
        Assert.Equal([CredentialLifecycleMutationPhase.Intent, CredentialLifecycleMutationPhase.LocatorUncertain], restarted.Operations.Select(item => item.LifecyclePhase).ToArray());
        Assert.Equal([AuditSchema.Actions.CredentialLifecycleIntent, AuditSchema.Actions.CredentialLifecycleOutcome], restarted.PendingAudits.Select(item => item.Action).ToArray());
        Assert.Equal(CredentialRegistryMutationStatus.Invalid, bypass.Status);
        Assert.Equal(CredentialFailureCode.Unauthorized, bypass.Failure!.Code);
        var competingResult = await service.ExecuteAsync(competing, destination =>
        {
            destination.Fill(2);
            return destination.Length;
        });
        Assert.Equal(CredentialLifecycleResultStatus.Conflict, competingResult.Status);
        Assert.Equal(2, (await restartedStore.ReadAsync()).RegistryRevision);
    }

    [Fact]
    public async Task PreparedCreateCanBeExplicitlyRepairedAcrossRestartWithoutLocatorLeakage()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var prepared = await SeedPreparedRegistrationAsync(paths);
        var store = Store(paths);
        Assert.Equal(2, prepared.RegistryRevision);
        var directTombstone = new CredentialRegistryMutation(CredentialRegistryMutationKind.Tombstone, Id("prepared-repair-bypass"), 2, ReferenceId(), null, null, null, null, null);
        var deniedTombstone = await store.MutateAsync(directTombstone);
        Assert.Equal(CredentialRegistryMutationStatus.Invalid, deniedTombstone.Status);
        Assert.Equal(CredentialFailureCode.Unauthorized, deniedTombstone.Failure!.Code);
        var service = ReconciliationService(paths);
        var preview = await service.PreviewAsync(new CredentialLifecyclePreviewRequest(Id("prepared-repair-cleanup"), CredentialLifecycleOperationKind.Repair, ReferenceId(), "workspace-1", Environment.UserName, 2));
        Assert.Equal(CredentialLifecyclePreviewStatus.Ready, preview.Status);
        var repair = new CredentialLifecycleRequest(CredentialLifecycleOperationKind.Repair, Id("prepared-repair-cleanup"), ReferenceId(), "workspace-1", Environment.UserName, 2, new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero), Preview: preview, Confirmed: true);
        Assert.Equal(CredentialLifecycleResultStatus.Applied, (await service.ExecuteAsync(repair)).Status);

        var repaired = await Store(paths).ReadAsync();
        Assert.Empty(repaired.Entries);
        Assert.False(Assert.Single(repaired.Tombstones).NeedsRepair);
        Assert.Empty(repaired.PendingAudits);
        Assert.Equal([CredentialLifecycleMutationPhase.Intent, CredentialLifecycleMutationPhase.LocatorPrepared, CredentialLifecycleMutationPhase.Intent, CredentialLifecycleMutationPhase.RepairComplete], repaired.Operations.Select(item => item.LifecyclePhase).ToArray());
        var publicArtifact = await File.ReadAllTextAsync(paths.CredentialRegistryDocumentPath);
        Assert.DoesNotContain(Locator().Value, publicArtifact, StringComparison.Ordinal);
        Assert.Contains("\"schemaVersion\": 1", publicArtifact, StringComparison.Ordinal);
        Assert.Contains("\"lifecycleShape\": 1", publicArtifact, StringComparison.Ordinal);
        Assert.Empty(JsonNode.Parse(await File.ReadAllTextAsync(paths.CredentialRegistryPrivateDocumentPath))!["locators"]!.AsArray());
    }

    [Fact]
    public async Task PublicLocatorUncertainCannotBeForgedAfterPreparedCreate()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var prepared = await SeedPreparedRegistrationAsync(paths);
        var store = Store(paths);
        var intent = Assert.Single(prepared.Operations, operation => operation.LifecyclePhase == CredentialLifecycleMutationPhase.Intent);
        var locatorUncertain = new CredentialRegistryMutation(CredentialRegistryMutationKind.RecordLocatorUncertain, Id("prepared-ack-uncertain"), 2, ReferenceId(), null, null, null, null, null, LifecycleOperation: (int)CredentialLifecycleOperationKind.Create, ActorId: Environment.UserName, LifecycleRequestHash: intent.LifecycleRequestHash, LifecyclePhase: CredentialLifecycleMutationPhase.LocatorUncertain, LifecycleIntentOperationId: intent.OperationId, WorkspaceId: "workspace-1", LifecycleAudit: AuditPayload(AuditSchema.Outcomes.Failed));

        var denied = await store.MutateAsync(locatorUncertain);
        var read = await Store(paths).ReadAsync();

        Assert.Equal(CredentialRegistryMutationStatus.Invalid, denied.Status);
        Assert.Equal(CredentialFailureCode.Unauthorized, denied.Failure!.Code);
        Assert.Equal(CredentialProviderHealthStatus.NeedsRepair, Assert.Single(read.Entries).Health);
        Assert.Equal([CredentialLifecycleMutationPhase.Intent, CredentialLifecycleMutationPhase.LocatorPrepared], read.Operations.Select(item => item.LifecyclePhase).ToArray());
        Assert.Equal([AuditSchema.Actions.CredentialLifecycleIntent], read.PendingAudits.Select(item => item.Action).ToArray());
        Assert.Single(JsonNode.Parse(await File.ReadAllTextAsync(paths.CredentialRegistryPrivateDocumentPath))!["locators"]!.AsArray());
        Assert.DoesNotContain(Locator().Value, await File.ReadAllTextAsync(paths.CredentialRegistryDocumentPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RawRepairAuthorityCannotBeForgedOrCompletedThroughPublicComposition()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        await SeedPreparedRegistrationAsync(paths);
        var store = Store(paths);
        var interruptedRepairId = Id("reconcile-prepared-interrupted");
        var interruptedRepair = new CredentialRegistryMutation(CredentialRegistryMutationKind.BeginRepair, interruptedRepairId, 2, ReferenceId(), null, null, null, null, null, LifecycleOperation: (int)CredentialLifecycleOperationKind.Repair, ActorId: Environment.UserName, PreviewHash: Hash('c'), LifecycleRequestHash: Hash('b'), LifecyclePhase: CredentialLifecycleMutationPhase.Intent, LifecycleIntentOperationId: interruptedRepairId, WorkspaceId: "workspace-1", LifecycleAudit: IntentAuditPayload());
        var forgedIntent = await store.MutateAsync(interruptedRepair);
        Assert.Equal(CredentialRegistryMutationStatus.Invalid, forgedIntent.Status);
        Assert.Equal(CredentialFailureCode.Unauthorized, forgedIntent.Failure!.Code);
        Assert.Equal(CredentialActorAuthentication.Unauthenticated, await store.AuthenticateActorAsync(Environment.UserName, CancellationToken.None));

        var service = ReconciliationService(paths);
        var preview = await service.PreviewAsync(new CredentialLifecyclePreviewRequest(Id("reconcile-prepared-terminal"), CredentialLifecycleOperationKind.ReconcileRepair, ReferenceId(), "workspace-1", Environment.UserName, 2, interruptedRepairId));
        Assert.Equal(CredentialLifecyclePreviewStatus.Conflict, preview.Status);
        var request = new CredentialLifecycleRequest(CredentialLifecycleOperationKind.ReconcileRepair, Id("reconcile-prepared-terminal"), ReferenceId(), "workspace-1", Environment.UserName, 2, new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero), Preview: preview, Confirmed: true, InterruptedRepairOperationId: interruptedRepairId);
        Assert.Equal(CredentialLifecycleResultStatus.Conflict, (await service.ExecuteAsync(request)).Status);
        var read = await Store(paths).ReadAsync();
        Assert.Equal(2, read.RegistryRevision);
        Assert.DoesNotContain(read.Operations, operation => operation.OperationId.Equals(interruptedRepairId) || operation.OperationId.Equals(request.OperationId));
    }

    [Fact]
    public async Task PublicStoreRejectsRepairAuthoritySemanticsAcrossEveryMutationKind()
    {
        using var workspace = new TestWorkspace();
        var store = Store(new WorkspacePaths(workspace.RootPath));

        foreach (var kind in Enum.GetValues<CredentialRegistryMutationKind>())
        {
            var operationId = Id($"repair-authority-{(int)kind}");
            var mutation = new CredentialRegistryMutation(kind, operationId, 0, ReferenceId(), null, null, null, null, null, LifecycleOperation: (int)CredentialLifecycleOperationKind.Repair, ActorId: Environment.UserName, PreviewHash: Hash('a'), LifecycleRequestHash: Hash('b'), LifecyclePhase: CredentialLifecycleMutationPhase.Intent, LifecycleIntentOperationId: operationId, WorkspaceId: "workspace-1", LifecycleAudit: IntentAuditPayload());
            var result = await store.MutateAsync(mutation);

            Assert.Equal(CredentialRegistryMutationStatus.Invalid, result.Status);
            Assert.Equal(CredentialFailureCode.Unauthorized, result.Failure!.Code);
        }

        Assert.Equal(0, (await store.ReadAsync()).RegistryRevision);
    }

    [Fact]
    public async Task PublicStoreDefaultsEveryUnclassifiedMutationKindToUnauthorized()
    {
        using var workspace = new TestWorkspace();
        var store = Store(new WorkspacePaths(workspace.RootPath));

        foreach (var kind in Enum.GetValues<CredentialRegistryMutationKind>())
        {
            var mutation = new CredentialRegistryMutation(kind, Id($"unclassified-{(int)kind}"), 0, ReferenceId(), null, null, null, null, null);
            var result = await store.MutateAsync(mutation);

            Assert.Equal(CredentialRegistryMutationStatus.Invalid, result.Status);
            Assert.Equal(CredentialFailureCode.Unauthorized, result.Failure!.Code);
        }

    }

    [Fact]
    public async Task PublicStoreCannotBypassLifecycleAuthorityWithValidDestructiveOrMetadataShapes()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = Store(paths);
        var rebound = Binding() with { Scope = Binding().Scope with { LoopRevision = 2 } };
        var revokedReference = Reference() with { Status = CredentialLifecycleStatus.Revoked, UpdatedAtUtc = new DateTimeOffset(2026, 8, 1, 12, 1, 0, TimeSpan.Zero) };
        var createOperationId = Id("public-create");
        var createHash = Hash('a');
        var metadataOperationId = Id("public-metadata-test");
        CredentialRegistryMutation[] bypasses =
        [
            Register(0),
            new(CredentialRegistryMutationKind.SetHealth, Id("public-health"), 0, ReferenceId(), null, null, null, CredentialProviderHealthStatus.Available, null),
            new(CredentialRegistryMutationKind.BeginCreate, createOperationId, 0, ReferenceId(), Reference(), Binding(), Id("public-create-consent"), CredentialProviderHealthStatus.NeedsRepair, null, false, (int)CredentialLifecycleOperationKind.Create, "user-1", null, createHash, CredentialLifecycleMutationPhase.Intent, createOperationId, null, "workspace-1", IntentAuditPayload()),
            Register(0) with { OperationId = Id("public-locator-prepared"), Health = CredentialProviderHealthStatus.NeedsRepair, LifecycleOperation = (int)CredentialLifecycleOperationKind.Create, ActorId = "user-1", LifecycleRequestHash = createHash, LifecyclePhase = CredentialLifecycleMutationPhase.LocatorPrepared, LifecycleIntentOperationId = createOperationId, WorkspaceId = "workspace-1" },
            new(CredentialRegistryMutationKind.RecordLocatorUncertain, Id("public-locator-uncertain"), 0, ReferenceId(), Reference(), Binding(), Id("public-create-consent"), CredentialProviderHealthStatus.NeedsRepair, null, false, (int)CredentialLifecycleOperationKind.Create, "user-1", null, createHash, CredentialLifecycleMutationPhase.LocatorUncertain, createOperationId, null, "workspace-1", AuditPayload(AuditSchema.Outcomes.Failed)),
            new(CredentialRegistryMutationKind.Tombstone, Id("public-tombstone"), 0, ReferenceId(), null, null, null, null, null),
            new(CredentialRegistryMutationKind.Bind, Id("public-bind"), 0, ReferenceId(), null, rebound, null, null, null, LifecycleOperation: (int)CredentialLifecycleOperationKind.Bind, ActorId: "user-1"),
            new(CredentialRegistryMutationKind.Consent, Id("public-consent"), 0, ReferenceId(), null, null, Id("public-consent-document"), null, null, true, (int)CredentialLifecycleOperationKind.Consent, "user-1"),
            new(CredentialRegistryMutationKind.UpdatePosture, Id("public-revoke"), 0, ReferenceId(), revokedReference, null, null, CredentialProviderHealthStatus.Revoked, null, LifecycleOperation: (int)CredentialLifecycleOperationKind.Revoke, ActorId: "user-1", PreviewHash: Hash('b'), AffectedActiveRuns: []),
            new(CredentialRegistryMutationKind.SetHealth, metadataOperationId, 0, ReferenceId(), null, null, null, CredentialProviderHealthStatus.Available, null, LifecycleOperation: (int)CredentialLifecycleOperationKind.Test, ActorId: "user-1", LifecycleRequestHash: Hash('c'), LifecyclePhase: CredentialLifecycleMutationPhase.MetadataComplete, LifecycleIntentOperationId: metadataOperationId, WorkspaceId: "workspace-1", LifecycleAudit: AuditPayload(AuditSchema.Outcomes.Succeeded)),
            new(CredentialRegistryMutationKind.BeginRepair, Id("public-repair"), 0, ReferenceId(), null, null, null, null, null, LifecycleOperation: (int)CredentialLifecycleOperationKind.Repair, ActorId: "user-1", PreviewHash: Hash('d'), LifecycleRequestHash: Hash('e'), LifecyclePhase: CredentialLifecycleMutationPhase.Intent, LifecycleIntentOperationId: Id("public-repair"), WorkspaceId: "workspace-1", LifecycleAudit: IntentAuditPayload())
        ];

        foreach (var bypass in bypasses)
        {
            var rejected = await store.MutateAsync(bypass);
            Assert.Equal(CredentialRegistryMutationStatus.Invalid, rejected.Status);
            Assert.Equal(CredentialFailureCode.Unauthorized, rejected.Failure!.Code);
        }

        var read = await Store(paths).ReadAsync();
        Assert.Equal(0, read.RegistryRevision);
        Assert.Empty(read.Entries);
        Assert.Empty(read.Operations);
        Assert.Empty(read.Tombstones);
        Assert.False(File.Exists(paths.CredentialRegistryDocumentPath));
        Assert.False(File.Exists(paths.CredentialRegistryPrivateDocumentPath));
    }

    [Fact]
    public async Task PublicStoreCannotSuppressPendingLifecycleAuditDelivery()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var seeded = await SeedRegistrationAsync(paths, auditLog: new FailingAuditLog());
        var pendingOperationIds = seeded.PendingAudits.Select(item => item.AuditOperationId).ToArray();
        Assert.NotEmpty(pendingOperationIds);

        var store = Store(paths);
        foreach (var operationId in pendingOperationIds)
        {
            Assert.False(await store.AcknowledgeAuditAsync(operationId));
        }

        Assert.Equal(pendingOperationIds, (await store.ReadAsync()).PendingAudits.Select(item => item.AuditOperationId).ToArray());

        var adapter = new CoordinatedCredentialCreateAdapter();
        var recovery = CredentialLifecyclePersistenceFactory.Create(paths, TestTrust(paths), adapter, new CountingCreateCredentialValueProvider(), adapter, new CapabilityDependentIndex([adapter]), adapter, new AuditLog(paths));
        await recovery.DrainAuditAsync();
        Assert.Empty((await store.ReadAsync()).PendingAudits);
    }

    [Fact]
    public async Task RawTombstoneRepairForgeryIsDeniedBeforeLegitimateRepair()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var seeded = await SeedRegistrationAsync(paths);
        var store = Store(paths);
        var seededRevision = seeded.RegistryRevision!.Value;
        var deleteService = ReconciliationService(paths, deleteSucceeds: false);
        var deletePreview = await deleteService.PreviewAsync(new CredentialLifecyclePreviewRequest(Id("reconcile-tombstone-delete"), CredentialLifecycleOperationKind.Delete, ReferenceId(), "workspace-1", Environment.UserName, seededRevision));
        var delete = new CredentialLifecycleRequest(CredentialLifecycleOperationKind.Delete, Id("reconcile-tombstone-delete"), ReferenceId(), "workspace-1", Environment.UserName, seededRevision, new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero), Preview: deletePreview, Confirmed: true);
        Assert.Equal(CredentialLifecycleResultStatus.NeedsRepair, (await deleteService.ExecuteAsync(delete)).Status);
        var tombstoneRevision = (await store.ReadAsync()).RegistryRevision!.Value;
        var originalTombstone = JsonNode.Parse(await File.ReadAllTextAsync(paths.CredentialRegistryDocumentPath))!["tombstones"]![0]!.ToJsonString();
        var interruptedRepairId = Id("reconcile-tombstone-interrupted");
        var interruptedRepair = new CredentialRegistryMutation(CredentialRegistryMutationKind.BeginRepair, interruptedRepairId, tombstoneRevision, ReferenceId(), null, null, null, null, null, LifecycleOperation: (int)CredentialLifecycleOperationKind.Repair, ActorId: Environment.UserName, PreviewHash: Hash('5'), LifecycleRequestHash: Hash('6'), LifecyclePhase: CredentialLifecycleMutationPhase.Intent, LifecycleIntentOperationId: interruptedRepairId, WorkspaceId: "workspace-1", LifecycleAudit: IntentAuditPayload());
        var forgedIntent = await store.MutateAsync(interruptedRepair);
        Assert.Equal(CredentialRegistryMutationStatus.Invalid, forgedIntent.Status);
        Assert.Equal(CredentialFailureCode.Unauthorized, forgedIntent.Failure!.Code);

        var service = ReconciliationService(paths);
        var reconcilePreview = await service.PreviewAsync(new CredentialLifecyclePreviewRequest(Id("reconcile-tombstone-terminal"), CredentialLifecycleOperationKind.ReconcileRepair, ReferenceId(), "workspace-1", Environment.UserName, tombstoneRevision, interruptedRepairId));
        Assert.Equal(CredentialLifecyclePreviewStatus.Conflict, reconcilePreview.Status);
        var repairPreview = await service.PreviewAsync(new CredentialLifecyclePreviewRequest(Id("reconcile-tombstone-repair"), CredentialLifecycleOperationKind.Repair, ReferenceId(), "workspace-1", Environment.UserName, tombstoneRevision));
        Assert.Equal(CredentialLifecyclePreviewStatus.Ready, repairPreview.Status);
        var repair = new CredentialLifecycleRequest(CredentialLifecycleOperationKind.Repair, Id("reconcile-tombstone-repair"), ReferenceId(), "workspace-1", Environment.UserName, tombstoneRevision, new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero), Preview: repairPreview, Confirmed: true);
        Assert.Equal(CredentialLifecycleResultStatus.Applied, (await service.ExecuteAsync(repair)).Status);

        var restarted = await Store(paths).ReadAsync();
        Assert.Empty(restarted.Entries);
        Assert.False(Assert.Single(restarted.Tombstones).NeedsRepair);
        Assert.Equal(originalTombstone, JsonNode.Parse(await File.ReadAllTextAsync(paths.CredentialRegistryDocumentPath))!["tombstones"]![0]!.ToJsonString());
        Assert.Empty(JsonNode.Parse(await File.ReadAllTextAsync(paths.CredentialRegistryPrivateDocumentPath))!["locators"]!.AsArray());
        Assert.Equal([CredentialLifecycleMutationPhase.Intent, CredentialLifecycleMutationPhase.TombstoneUncertain, CredentialLifecycleMutationPhase.Intent, CredentialLifecycleMutationPhase.RepairComplete], restarted.Operations.Where(operation => operation.LifecyclePhase is not null).Select(operation => operation.LifecyclePhase).TakeLast(4).ToArray());
        Assert.Empty(restarted.PendingAudits);
    }

    [Fact]
    public async Task UncertainTombstoneRetainsPrivateLocatorAcrossRestartUntilExplicitRepairCompletion()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var seeded = await SeedRegistrationAsync(paths);
        var store = Store(paths);
        var seededRevision = seeded.RegistryRevision!.Value;
        var deleteService = ReconciliationService(paths, deleteSucceeds: false);
        var deletePreview = await deleteService.PreviewAsync(new CredentialLifecyclePreviewRequest(Id("repair-delete-intent"), CredentialLifecycleOperationKind.Delete, ReferenceId(), "workspace-1", Environment.UserName, seededRevision));
        var delete = new CredentialLifecycleRequest(CredentialLifecycleOperationKind.Delete, Id("repair-delete-intent"), ReferenceId(), "workspace-1", Environment.UserName, seededRevision, new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero), Preview: deletePreview, Confirmed: true);
        Assert.Equal(CredentialLifecycleResultStatus.NeedsRepair, (await deleteService.ExecuteAsync(delete)).Status);
        var tombstoneRevision = (await store.ReadAsync()).RegistryRevision!.Value;
        var originalTombstone = JsonNode.Parse(await File.ReadAllTextAsync(paths.CredentialRegistryDocumentPath))!["tombstones"]![0]!.ToJsonString();

        var restarted = await Store(paths).ReadAsync();
        var repairRequired = Assert.Single(restarted.Tombstones);
        Assert.True(repairRequired.NeedsRepair);
        Assert.NotNull(repairRequired.RepairBinding);
        var retainedLocator = Assert.Single(JsonNode.Parse(await File.ReadAllTextAsync(paths.CredentialRegistryPrivateDocumentPath))!["locators"]!.AsArray());
        Assert.Equal(Locator().Value, retainedLocator!["locator"]!.GetValue<string>());

        var uncertainService = ReconciliationService(paths, deleteSucceeds: false);
        var uncertainPreview = await uncertainService.PreviewAsync(new CredentialLifecyclePreviewRequest(Id("repair-uncertain-intent"), CredentialLifecycleOperationKind.Repair, ReferenceId(), "workspace-1", Environment.UserName, tombstoneRevision));
        Assert.Equal(CredentialLifecyclePreviewStatus.Ready, uncertainPreview.Status);
        var uncertainRepair = new CredentialLifecycleRequest(CredentialLifecycleOperationKind.Repair, Id("repair-uncertain-intent"), ReferenceId(), "workspace-1", Environment.UserName, tombstoneRevision, new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero), Preview: uncertainPreview, Confirmed: true);
        Assert.Equal(CredentialLifecycleResultStatus.NeedsRepair, (await uncertainService.ExecuteAsync(uncertainRepair)).Status);
        var uncertain = await Store(paths).ReadAsync();
        Assert.True(Assert.Single(uncertain.Tombstones).NeedsRepair);
        var finalService = ReconciliationService(paths);
        var finalPreview = await finalService.PreviewAsync(new CredentialLifecyclePreviewRequest(Id("repair-explicit-intent"), CredentialLifecycleOperationKind.Repair, ReferenceId(), "workspace-1", Environment.UserName, uncertain.RegistryRevision!.Value));
        Assert.Equal(CredentialLifecyclePreviewStatus.Ready, finalPreview.Status);
        var finalRepair = new CredentialLifecycleRequest(CredentialLifecycleOperationKind.Repair, Id("repair-explicit-intent"), ReferenceId(), "workspace-1", Environment.UserName, uncertain.RegistryRevision.Value, new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero), Preview: finalPreview, Confirmed: true);
        Assert.Equal(CredentialLifecycleResultStatus.Applied, (await finalService.ExecuteAsync(finalRepair)).Status);

        var completed = await Store(paths).ReadAsync();
        Assert.False(Assert.Single(completed.Tombstones).NeedsRepair);
        Assert.Equal(originalTombstone, JsonNode.Parse(await File.ReadAllTextAsync(paths.CredentialRegistryDocumentPath))!["tombstones"]![0]!.ToJsonString());
        Assert.Empty(JsonNode.Parse(await File.ReadAllTextAsync(paths.CredentialRegistryPrivateDocumentPath))!["locators"]!.AsArray());
        Assert.Empty(completed.PendingAudits);
        Assert.False(await Store(paths).AcknowledgeAuditAsync(Id("repair-unknown-audit")));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.False(await Store(paths).AcknowledgeAuditAsync(finalRepair.OperationId, canceled.Token));
        var acknowledged = await Store(paths).ReadAsync();
        Assert.Equal(completed.RegistryRevision, acknowledged.RegistryRevision);
        Assert.Empty(acknowledged.PendingAudits);
        Assert.Equal(originalTombstone, JsonNode.Parse(await File.ReadAllTextAsync(paths.CredentialRegistryDocumentPath))!["tombstones"]![0]!.ToJsonString());
    }

    [Fact]
    public async Task SetHealthCannotWidenRestrictiveReferencePosture()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var seeded = await SeedRegistrationAsync(paths);
        var store = Store(paths);
        var seededRevision = seeded.RegistryRevision!.Value;
        var service = ReconciliationService(paths);
        var preview = await service.PreviewAsync(new CredentialLifecyclePreviewRequest(Id("disable-safe"), CredentialLifecycleOperationKind.Disable, ReferenceId(), "workspace-1", Environment.UserName, seededRevision));
        var disable = new CredentialLifecycleRequest(CredentialLifecycleOperationKind.Disable, Id("disable-safe"), ReferenceId(), "workspace-1", Environment.UserName, seededRevision, new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero), Preview: preview, Confirmed: true);
        Assert.Equal(CredentialLifecycleResultStatus.Applied, (await service.ExecuteAsync(disable)).Status);

        var disabledRevision = (await store.ReadAsync()).RegistryRevision!.Value;
        var widened = await store.MutateAsync(new CredentialRegistryMutation(CredentialRegistryMutationKind.SetHealth, Id("widen-health"), disabledRevision, ReferenceId(), null, null, null, CredentialProviderHealthStatus.Available, null));

        Assert.Equal(CredentialRegistryMutationStatus.Invalid, widened.Status);
        Assert.Equal(CredentialFailureCode.Unauthorized, widened.Failure!.Code);
        var entry = Assert.Single((await store.ReadAsync()).Entries);
        Assert.Equal(CredentialLifecycleStatus.Disabled, entry.Reference.Status);
        Assert.Equal(CredentialProviderHealthStatus.Disabled, entry.Health);
    }

    [Theory]
    [InlineData(CredentialLifecycleOperationKind.Expire, CredentialLifecycleStatus.Expired, CredentialProviderHealthStatus.Expired)]
    [InlineData(CredentialLifecycleOperationKind.Revoke, CredentialLifecycleStatus.Revoked, CredentialProviderHealthStatus.Revoked)]
    public async Task MetadataPostureTransitionsPersistExactRestrictiveReferenceAndHealthAcrossRestart(CredentialLifecycleOperationKind kind, CredentialLifecycleStatus expectedStatus, CredentialProviderHealthStatus expectedHealth)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var seeded = await SeedRegistrationAsync(paths);
        var service = ReconciliationService(paths);
        var operationId = Id($"metadata-{kind.ToString().ToLowerInvariant()}");
        var preview = await service.PreviewAsync(new CredentialLifecyclePreviewRequest(operationId, kind, ReferenceId(), "workspace-1", Environment.UserName, seeded.RegistryRevision!.Value));
        var request = new CredentialLifecycleRequest(kind, operationId, ReferenceId(), "workspace-1", Environment.UserName, seeded.RegistryRevision.Value, new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero), Preview: preview, Confirmed: true);

        var applied = await service.ExecuteAsync(request);
        var restarted = await Store(paths).ReadAsync();
        var replayed = await ReconciliationService(paths).ExecuteAsync(request);

        Assert.Equal(CredentialLifecycleResultStatus.Applied, applied.Status);
        Assert.Equal(expectedHealth, applied.Health);
        var entry = Assert.Single(restarted.Entries);
        Assert.Equal(expectedStatus, entry.Reference.Status);
        Assert.Equal(expectedHealth, entry.Health);
        var terminal = Assert.Single(restarted.Operations, operation => operation.OperationId.Equals(request.OperationId));
        Assert.Equal(CredentialLifecycleMutationPhase.MetadataComplete, terminal.LifecyclePhase);
        Assert.Equal(CredentialLifecycleResultStatus.Replayed, replayed.Status);
    }

    [Fact]
    public async Task RawProviderIntentAndConsentCannotBeIntroducedByDirectStoreCalls()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var seeded = await SeedRegistrationAsync(paths);
        var store = Store(paths);
        var seededRevision = seeded.RegistryRevision!.Value;
        var intentId = Id("store-unresolved-intent");
        var intent = new CredentialRegistryMutation(CredentialRegistryMutationKind.UpdatePosture, intentId, seededRevision, ReferenceId(), Reference(), null, null, CredentialProviderHealthStatus.NeedsRepair, null, LifecycleOperation: (int)CredentialLifecycleOperationKind.Replace, ActorId: "user-1", PreviewHash: Hash('4'), LifecycleRequestHash: Hash('5'), LifecyclePhase: CredentialLifecycleMutationPhase.Intent, LifecycleIntentOperationId: intentId, WorkspaceId: "workspace-1", LifecycleAudit: IntentAuditPayload());
        var deniedIntent = await store.MutateAsync(intent);
        var consent = await store.MutateAsync(new CredentialRegistryMutation(CredentialRegistryMutationKind.Consent, Id("store-unresolved-consent"), seededRevision, ReferenceId(), null, null, Id("store-unresolved-consent-document"), null, null, true));

        Assert.Equal(CredentialRegistryMutationStatus.Invalid, deniedIntent.Status);
        Assert.Equal(CredentialFailureCode.Unauthorized, deniedIntent.Failure!.Code);
        Assert.Equal(CredentialRegistryMutationStatus.Invalid, consent.Status);
        Assert.Equal(CredentialFailureCode.Unauthorized, consent.Failure!.Code);
        Assert.Equal(seededRevision, (await store.ReadAsync()).RegistryRevision);
        Assert.Equal(CredentialProviderHealthStatus.Available, Assert.Single((await store.ReadAsync()).Entries).Health);
    }

    [Fact]
    public async Task RepairMutationShapesAndMissingTombstoneFailClosed()
    {
        using var workspace = new TestWorkspace();
        var store = Store(new WorkspacePaths(workspace.RootPath));
        var intentId = Id("missing-repair-intent");
        var missingTombstone = new CredentialRegistryMutation(CredentialRegistryMutationKind.BeginRepair, intentId, 0, ReferenceId(), null, null, null, null, null, LifecycleOperation: (int)CredentialLifecycleOperationKind.Repair, ActorId: "user-1", LifecycleRequestHash: Hash('7'), LifecyclePhase: CredentialLifecycleMutationPhase.Intent, LifecycleIntentOperationId: intentId, WorkspaceId: "workspace-1", LifecycleAudit: IntentAuditPayload());
        var extraReference = missingTombstone with { OperationId = Id("invalid-repair-reference"), LifecycleIntentOperationId = Id("invalid-repair-reference"), Reference = Reference() };
        var missingWorkspace = missingTombstone with { OperationId = Id("invalid-repair-workspace"), LifecycleIntentOperationId = Id("invalid-repair-workspace"), WorkspaceId = null };
        var missingIntentAudit = missingTombstone with { OperationId = Id("invalid-repair-audit-missing"), LifecycleIntentOperationId = Id("invalid-repair-audit-missing"), LifecycleAudit = null };
        var wrongIntentAudit = missingTombstone with { OperationId = Id("invalid-repair-audit-action"), LifecycleIntentOperationId = Id("invalid-repair-audit-action"), LifecycleAudit = AuditPayload(AuditSchema.Outcomes.Started) };

        foreach (var mutation in new[] { missingTombstone, extraReference, missingWorkspace, missingIntentAudit, wrongIntentAudit })
        {
            var result = await store.MutateAsync(mutation);
            Assert.Equal(CredentialRegistryMutationStatus.Invalid, result.Status);
            Assert.Equal(CredentialFailureCode.Unauthorized, result.Failure!.Code);
        }
        Assert.Equal(0, (await store.ReadAsync()).RegistryRevision);
    }

    [Fact]
    public async Task Restart_readback_preserves_safe_state_evidence_and_tombstone()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var registered = await SeedRegistrationAsync(paths);
        Assert.Equal(3, registered.RegistryRevision);

        var binding = Binding() with { Scope = Binding().Scope with { ActorId = Environment.UserName } };
        var evidence = Evidence(binding);
        Assert.True((await Store(paths, new FixedTimeProvider()).AppendAsync(evidence, default)).Succeeded);
        var service = ReconciliationService(paths);
        var revision = (await Store(paths).ReadAsync()).RegistryRevision!.Value;
        var preview = await service.PreviewAsync(new CredentialLifecyclePreviewRequest(Id("tombstone-1"), CredentialLifecycleOperationKind.Delete, ReferenceId(), "workspace-1", Environment.UserName, revision));
        var delete = new CredentialLifecycleRequest(CredentialLifecycleOperationKind.Delete, Id("tombstone-1"), ReferenceId(), "workspace-1", Environment.UserName, revision, new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero), Preview: preview, Confirmed: true);
        Assert.Equal(CredentialLifecycleResultStatus.Applied, (await service.ExecuteAsync(delete)).Status);

        var restarted = await Store(paths).ReadAsync();
        Assert.True(restarted.Succeeded);
        Assert.Equal(revision + 2, restarted.RegistryRevision);
        Assert.Empty(restarted.Entries);
        var savedTombstone = Assert.Single(restarted.Tombstones);
        Assert.Equal("credential-1", savedTombstone.ReferenceId.Value);
        var terminal = Assert.Single(restarted.Operations, operation => operation.LifecyclePhase == CredentialLifecycleMutationPhase.TombstoneComplete);
        Assert.Equal(terminal.OperationId, savedTombstone.OperationId);
        Assert.Equal(delete.OperationId, terminal.LifecycleIntentOperationId);
        Assert.Contains(restarted.Operations, operation => operation.LifecyclePhase == CredentialLifecycleMutationPhase.Complete);
        Assert.Contains(restarted.Operations, operation => operation.OperationId.Value == "evidence-1");
        Assert.Equal("evidence-1", Assert.Single(restarted.Evidence).EvidenceId.Value);
    }

    [Fact]
    public async Task Retry_and_stale_or_changed_operation_fail_closed()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var first = await SeedRegistrationAsync(paths);
        var adapter = new CoordinatedCredentialCreateAdapter();
        var provider = new CountingCreateCredentialValueProvider();
        var service = CredentialLifecyclePersistenceFactory.Create(paths, TestTrust(paths), adapter, provider, adapter, new CapabilityDependentIndex([adapter]), adapter, new AuditLog(paths));
        var reference = Reference() with { OwnerId = Environment.UserName };
        var binding = Binding() with { Scope = Binding().Scope with { ActorId = Environment.UserName } };
        var exact = new CredentialLifecycleRequest(CredentialLifecycleOperationKind.Create, Id("seed-register-1"), ReferenceId(), "workspace-1", Environment.UserName, 0, new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero), 4, reference, binding, Id("seed-consent-1"));

        var replay = await service.ExecuteAsync(exact, destination =>
        {
            destination.Fill(1);
            return destination.Length;
        });
        var stale = await service.ExecuteAsync(exact with { OperationId = Id("stale-create") }, destination => destination.Length);
        var changed = await service.ExecuteAsync(exact with { ValueByteLength = 5 }, destination => destination.Length);

        Assert.Equal(3, first.RegistryRevision);
        Assert.Equal(CredentialLifecycleResultStatus.Replayed, replay.Status);
        Assert.Equal(CredentialLifecycleResultStatus.Conflict, stale.Status);
        Assert.Equal(CredentialLifecycleResultStatus.Conflict, changed.Status);
        Assert.Equal(0, adapter.CreateCount);
        Assert.Equal(0, provider.CreateCount);
    }

    [Fact]
    public async Task Partial_primary_recovers_only_from_last_proved_pair_and_workspace_substitution_fails()
    {
        using var source = new TestWorkspace();
        var sourcePaths = new WorkspacePaths(source.RootPath);
        var sourceStore = Store(sourcePaths);
        await SeedRegistrationAsync(sourcePaths);
        var binding = Binding() with { Scope = Binding().Scope with { ActorId = Environment.UserName } };
        Assert.True((await sourceStore.AppendAsync(Evidence(binding), default)).Succeeded);

        await File.WriteAllTextAsync(sourcePaths.CredentialRegistryPrivateDocumentPath, "{");
        var recovered = await Store(sourcePaths).ReadAsync();
        Assert.True(recovered.Succeeded);
        Assert.Equal(3, recovered.RegistryRevision);
        Assert.Equal(CredentialProviderHealthStatus.Available, Assert.Single(recovered.Entries).Health);

        using var destination = new TestWorkspace();
        var destinationPaths = new WorkspacePaths(destination.RootPath);
        Directory.CreateDirectory(destinationPaths.CredentialRegistryPath);
        Directory.CreateDirectory(destinationPaths.CredentialRegistryPrivatePath);
        File.Copy(sourcePaths.CredentialRegistryProofPath, destinationPaths.CredentialRegistryDocumentPath);
        File.Copy(sourcePaths.CredentialRegistryPrivateProofPath, destinationPaths.CredentialRegistryPrivateDocumentPath);
        var substituted = await Store(destinationPaths).ReadAsync();
        Assert.False(substituted.Succeeded);
        Assert.Equal(CredentialFailureCode.Unavailable, substituted.Failure!.Code);
    }

    [Fact]
    public async Task Public_artifacts_never_contain_locator_or_submitted_secret_canaries()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = Store(paths);
        await SeedRegistrationAsync(paths);
        var publicText = await File.ReadAllTextAsync(paths.CredentialRegistryDocumentPath);
        var privateText = await File.ReadAllTextAsync(paths.CredentialRegistryPrivateDocumentPath);
        Assert.DoesNotContain(Locator().Value, publicText, StringComparison.Ordinal);
        Assert.Contains(Locator().Value, privateText, StringComparison.Ordinal);
        Assert.DoesNotContain("plaintext-secret-canary", publicText, StringComparison.Ordinal);
        Assert.DoesNotContain("ciphertext-envelope-canary", publicText, StringComparison.Ordinal);
        Assert.DoesNotContain("key-material-canary", publicText, StringComparison.Ordinal);

        var unsafeLocator = new CredentialRegistryMutation(CredentialRegistryMutationKind.Register, Id("unsafe-1"), 1, ReferenceId(), Reference(), Binding(), Id("consent-1"), CredentialProviderHealthStatus.Available, null);
        var rejected = await store.MutateAsync(unsafeLocator);
        Assert.Equal(CredentialRegistryMutationStatus.Invalid, rejected.Status);
        Assert.Equal(CredentialFailureCode.Unauthorized, rejected.Failure!.Code);
    }

    [Fact]
    public async Task Concurrent_optimistic_mutations_admit_exactly_one_writer()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        async Task<CredentialLifecycleResult> CreateAsync()
        {
            var adapter = new CoordinatedCredentialCreateAdapter();
            var provider = new CountingCreateCredentialValueProvider();
            var service = CredentialLifecyclePersistenceFactory.Create(paths, TestTrust(paths), adapter, provider, adapter, new CapabilityDependentIndex([adapter]), adapter, new AuditLog(paths));
            var reference = Reference() with { OwnerId = Environment.UserName };
            var binding = Binding() with { Scope = Binding().Scope with { ActorId = Environment.UserName } };
            return await service.ExecuteAsync(new CredentialLifecycleRequest(CredentialLifecycleOperationKind.Create, Id("concurrent-create"), ReferenceId(), "workspace-1", Environment.UserName, 0, new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero), 4, reference, binding, Id("concurrent-consent")), destination =>
            {
                destination.Fill(1);
                return destination.Length;
            });
        }

        var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => CreateAsync()));
        Assert.Equal(1, attempts.Count(item => item.Status == CredentialLifecycleResultStatus.Applied));
        Assert.Equal(7, attempts.Count(item => item.Status == CredentialLifecycleResultStatus.Replayed));
        Assert.Equal(3, (await Store(paths).ReadAsync()).RegistryRevision);
    }

    [Fact]
    public async Task Unsupported_or_fully_corrupt_artifacts_fail_closed_without_plaintext_fallback()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        await SeedRegistrationAsync(paths);
        await File.WriteAllTextAsync(paths.CredentialRegistryDocumentPath, "{\"schemaVersion\":2}");
        await File.WriteAllTextAsync(paths.CredentialRegistryPrivateDocumentPath, "{\"schemaVersion\":2}");
        await File.WriteAllTextAsync(paths.CredentialRegistryProofPath, "plaintext-secret-canary");
        await File.WriteAllTextAsync(paths.CredentialRegistryPrivateProofPath, "ciphertext-envelope-canary");

        var read = await Store(paths).ReadAsync();
        Assert.False(read.Succeeded);
        Assert.Equal(CredentialFailureCode.Unavailable, read.Failure!.Code);
        var mutation = await ReconciliationService(paths).ExecuteAsync(new CredentialLifecycleRequest(CredentialLifecycleOperationKind.Delete, Id("corrupt-delete"), ReferenceId(), "workspace-1", Environment.UserName, 3, new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero), Confirmed: true));
        Assert.Equal(CredentialLifecycleResultStatus.Unavailable, mutation.Status);
        var corruptStore = Store(paths);
        Assert.Equal(CredentialFailureCode.Unavailable, (await corruptStore.GetAsync(ReferenceId(), default)).Failure!.Code);
        Assert.False(await corruptStore.AcknowledgeAuditAsync(Id("corrupt-audit")));
        Assert.Equal(CredentialFailureCode.Unavailable, (await corruptStore.AppendAsync(Evidence(Binding()), default)).Failure!.Code);
    }

    [Fact]
    public async Task Lifecycle_audit_drain_acknowledgement_is_durable_and_idempotent()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var seeded = await SeedRegistrationAsync(paths, auditLog: new FailingAuditLog());
        Assert.NotEmpty(seeded.PendingAudits);
        var store = Store(paths);
        Assert.False(await store.AcknowledgeAuditAsync(seeded.PendingAudits[0].AuditOperationId));

        var adapter = new CoordinatedCredentialCreateAdapter();
        var audit = new AuditLog(paths);
        var service = CredentialLifecyclePersistenceFactory.Create(paths, TestTrust(paths), adapter, new CountingCreateCredentialValueProvider(), adapter, new CapabilityDependentIndex([adapter]), adapter, audit);
        await service.DrainAuditAsync();
        Assert.Empty((await Store(paths).ReadAsync()).PendingAudits);
        var delivered = await audit.ReadTailAsync(10);
        Assert.NotEmpty(delivered);

        var restarted = CredentialLifecyclePersistenceFactory.Create(paths, TestTrust(paths), adapter, new CountingCreateCredentialValueProvider(), adapter, new CapabilityDependentIndex([adapter]), adapter, audit);
        await restarted.DrainAuditAsync();
        var replayedDrain = await audit.ReadTailAsync(10);
        Assert.Equal(delivered.Count, replayedDrain.Count);
        Assert.Equal(delivered.Select(item => (item.Action, item.Target, item.Outcome, item.Detail)), replayedDrain.Select(item => (item.Action, item.Target, item.Outcome, item.Detail)));
    }

    [Fact]
    public async Task AuthenticatedPriorSchemaOneShapeIsRejectedWithoutRewriteOrMigration()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        await SeedRegistrationAsync(paths);
        var publicNode = JsonNode.Parse(await File.ReadAllTextAsync(paths.CredentialRegistryDocumentPath))!.AsObject();
        var privateNode = JsonNode.Parse(await File.ReadAllTextAsync(paths.CredentialRegistryPrivateDocumentPath))!.AsObject();
        Assert.True(publicNode.Remove("auditDeliveries"));
        foreach (var operation in publicNode["operations"]!.AsArray())
        {
            Assert.True(operation!.AsObject().Remove("workspaceId"));
            Assert.True(operation.AsObject().Remove("auditOutbox"));
        }
        publicNode["stateDigest"] = ComputeStateDigest(publicNode, privateNode);
        privateNode["stateDigest"] = publicNode["stateDigest"]!.GetValue<string>();
        publicNode["contentDigest"] = ComputeContentDigest(publicNode);
        var workspaceIdentity = publicNode["workspaceIdentity"]!.GetValue<string>();
        var generation = publicNode["generation"]!.GetValue<long>();
        var contentDigest = publicNode["contentDigest"]!.GetValue<string>();
        var priorShapeTrust = new TestCapabilityLifecycleTrustProvider();
        _ = await priorShapeTrust.InitializeAsync(workspaceIdentity, generation, contentDigest);
        publicNode["authenticationTag"] = await priorShapeTrust.AuthenticateArtifactAsync(workspaceIdentity, generation, contentDigest);
        Assert.True(await priorShapeTrust.VerifyArtifactAsync(workspaceIdentity, generation, contentDigest, publicNode["authenticationTag"]!.GetValue<string>()));
        await File.WriteAllTextAsync(paths.CredentialRegistryDocumentPath, publicNode.ToJsonString(JsonOptions(writeIndented: true)));
        await File.WriteAllTextAsync(paths.CredentialRegistryPrivateDocumentPath, privateNode.ToJsonString(JsonOptions(writeIndented: true)));
        var originalPublic = await File.ReadAllBytesAsync(paths.CredentialRegistryDocumentPath);
        var originalPrivate = await File.ReadAllBytesAsync(paths.CredentialRegistryPrivateDocumentPath);

        var read = await new CredentialRegistryStore(paths, priorShapeTrust, new AcceptingLocatorVerifier()).ReadAsync();

        Assert.False(read.Succeeded);
        Assert.Equal(CredentialFailureCode.Unavailable, read.Failure!.Code);
        Assert.Equal(originalPublic, await File.ReadAllBytesAsync(paths.CredentialRegistryDocumentPath));
        Assert.Equal(originalPrivate, await File.ReadAllBytesAsync(paths.CredentialRegistryPrivateDocumentPath));
    }

    [Fact]
    public async Task AuthenticatedSchemaOneOutboxWithoutActionIsRejectedWithoutRewriteOrMigration()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        await SeedRegistrationAsync(paths, auditLog: new FailingAuditLog());
        var publicNode = JsonNode.Parse(await File.ReadAllTextAsync(paths.CredentialRegistryDocumentPath))!.AsObject();
        var privateNode = JsonNode.Parse(await File.ReadAllTextAsync(paths.CredentialRegistryPrivateDocumentPath))!.AsObject();
        var operationWithOutbox = publicNode["operations"]!.AsArray().First(operation => operation?["auditOutbox"] is not null);
        var outbox = operationWithOutbox!["auditOutbox"]!.AsObject();
        Assert.True(outbox.Remove("action"));
        publicNode["stateDigest"] = ComputeStateDigest(publicNode, privateNode);
        privateNode["stateDigest"] = publicNode["stateDigest"]!.GetValue<string>();
        publicNode["contentDigest"] = ComputeContentDigest(publicNode);
        var workspaceIdentity = publicNode["workspaceIdentity"]!.GetValue<string>();
        var generation = publicNode["generation"]!.GetValue<long>();
        var contentDigest = publicNode["contentDigest"]!.GetValue<string>();
        var priorShapeTrust = new TestCapabilityLifecycleTrustProvider();
        _ = await priorShapeTrust.InitializeAsync(workspaceIdentity, generation, contentDigest);
        publicNode["authenticationTag"] = await priorShapeTrust.AuthenticateArtifactAsync(workspaceIdentity, generation, contentDigest);
        Assert.True(await priorShapeTrust.VerifyArtifactAsync(workspaceIdentity, generation, contentDigest, publicNode["authenticationTag"]!.GetValue<string>()));
        await File.WriteAllTextAsync(paths.CredentialRegistryDocumentPath, publicNode.ToJsonString(JsonOptions(writeIndented: true)));
        await File.WriteAllTextAsync(paths.CredentialRegistryPrivateDocumentPath, privateNode.ToJsonString(JsonOptions(writeIndented: true)));
        var originalPublic = await File.ReadAllBytesAsync(paths.CredentialRegistryDocumentPath);
        var originalPrivate = await File.ReadAllBytesAsync(paths.CredentialRegistryPrivateDocumentPath);

        var read = await new CredentialRegistryStore(paths, priorShapeTrust, new AcceptingLocatorVerifier()).ReadAsync();

        Assert.False(read.Succeeded);
        Assert.Equal(CredentialFailureCode.Unavailable, read.Failure!.Code);
        Assert.Equal(originalPublic, await File.ReadAllBytesAsync(paths.CredentialRegistryDocumentPath));
        Assert.Equal(originalPrivate, await File.ReadAllBytesAsync(paths.CredentialRegistryPrivateDocumentPath));
    }

    [Theory]
    [InlineData("entry-health")]
    [InlineData("entry-order")]
    [InlineData("locator-order")]
    [InlineData("duplicate-locator")]
    [InlineData("operation-id")]
    [InlineData("duplicate-operation")]
    [InlineData("unexpected-audit-outbox")]
    [InlineData("invalid-audit-delivery")]
    [InlineData("invalid-evidence")]
    [InlineData("invalid-tombstone")]
    [InlineData("invalid-lifecycle-shape")]
    public async Task Authenticated_structural_corruption_is_rejected_without_using_the_proof_as_a_migration(string corruption)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var first = await SeedRegistrationAsync(paths, 1);
        var second = await SeedRegistrationAsync(paths, 2, expectedRegistryRevision: first.RegistryRevision!.Value);
        if (corruption == "invalid-tombstone")
        {
            var service = ReconciliationService(paths);
            var preview = await service.PreviewAsync(new CredentialLifecyclePreviewRequest(Id("corrupt-tombstone"), CredentialLifecycleOperationKind.Delete, ReferenceId(1), "workspace-1", Environment.UserName, second.RegistryRevision!.Value));
            var delete = new CredentialLifecycleRequest(CredentialLifecycleOperationKind.Delete, Id("corrupt-tombstone"), ReferenceId(1), "workspace-1", Environment.UserName, second.RegistryRevision.Value, new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero), Preview: preview, Confirmed: true);
            Assert.Equal(CredentialLifecycleResultStatus.Applied, (await service.ExecuteAsync(delete)).Status);
        }

        var publicNode = JsonNode.Parse(await File.ReadAllTextAsync(paths.CredentialRegistryDocumentPath))!.AsObject();
        var privateNode = JsonNode.Parse(await File.ReadAllTextAsync(paths.CredentialRegistryPrivateDocumentPath))!.AsObject();
        CorruptRegistry(publicNode, privateNode, corruption);
        publicNode["stateDigest"] = ComputeStateDigest(publicNode, privateNode);
        privateNode["stateDigest"] = publicNode["stateDigest"]!.GetValue<string>();
        publicNode["contentDigest"] = ComputeContentDigest(publicNode);
        var identity = publicNode["workspaceIdentity"]!.GetValue<string>();
        var generation = publicNode["generation"]!.GetValue<long>();
        var contentDigest = publicNode["contentDigest"]!.GetValue<string>();
        var corruptedTrust = new TestCapabilityLifecycleTrustProvider();
        _ = await corruptedTrust.InitializeAsync(identity, generation, contentDigest);
        publicNode["authenticationTag"] = await corruptedTrust.AuthenticateArtifactAsync(identity, generation, contentDigest);
        await File.WriteAllTextAsync(paths.CredentialRegistryDocumentPath, publicNode.ToJsonString(JsonOptions(writeIndented: true)));
        await File.WriteAllTextAsync(paths.CredentialRegistryPrivateDocumentPath, privateNode.ToJsonString(JsonOptions(writeIndented: true)));

        var read = await new CredentialRegistryStore(paths, corruptedTrust, new AcceptingLocatorVerifier()).ReadAsync();

        Assert.False(read.Succeeded);
        Assert.Equal(CredentialFailureCode.Unavailable, read.Failure!.Code);
    }

    [Fact]
    public async Task Evidence_is_bound_to_a_live_exact_registered_reference()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = Store(paths);
        var binding = Binding() with { Scope = Binding().Scope with { ActorId = Environment.UserName } };
        Assert.False((await store.AppendAsync(Evidence(binding), default)).Succeeded);
        await SeedRegistrationAsync(paths);
        Assert.True((await store.AppendAsync(Evidence(binding), default)).Succeeded);
    }

    [Fact]
    public async Task Registry_fails_closed_without_committing_when_a_trust_provider_exceeds_its_declared_tag_bound()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        await SeedRegistrationAsync(paths);
        var before = await Store(paths).ReadAsync();
        var binding = Binding() with { Scope = Binding().Scope with { ActorId = Environment.UserName } };
        var store = new CredentialRegistryStore(paths, new OversizedAuthenticationTagTrustProvider(TestTrust(paths)), new AcceptingLocatorVerifier());

        var result = await store.AppendAsync(Evidence(binding), CancellationToken.None);
        var after = await Store(paths).ReadAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(CredentialFailureCode.Unavailable, result.Failure!.Code);
        Assert.Equal(before.RegistryRevision, after.RegistryRevision);
        Assert.Empty(after.Evidence);
    }

    [Fact]
    public async Task Evidence_scope_must_be_equal_to_or_narrower_than_the_registered_binding()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = Store(paths);
        var binding = Binding() with { Scope = Binding().Scope with { ActorId = Environment.UserName } };
        var seeded = await SeedRegistrationAsync(paths);

        var broaderScopes = new[]
        {
            binding.Scope with { WorkspaceId = "workspace-2" },
            binding.Scope with { RoleId = null, LoopId = null, LoopRevision = null, NodeId = null },
            binding.Scope with { Target = null },
            binding.Scope with { Capability = null, Implementation = null }
        };
        for (var index = 0; index < broaderScopes.Length; index++)
        {
            var rejected = await store.AppendAsync(Evidence(binding, $"broader-{index}", broaderScopes[index]), default);
            Assert.False(rejected.Succeeded);
            Assert.Equal(CredentialFailureCode.Unauthorized, rejected.Failure!.Code);
        }

        var narrower = binding.Scope with { NotBeforeUtc = new DateTimeOffset(2026, 8, 1, 11, 0, 0, TimeSpan.Zero), NotAfterUtc = new DateTimeOffset(2026, 8, 1, 13, 0, 0, TimeSpan.Zero) };
        Assert.True((await store.AppendAsync(Evidence(binding, "narrower-1", narrower), default)).Succeeded);
        var current = await store.ReadAsync();
        Assert.Equal(seeded.RegistryRevision!.Value + 1, current.RegistryRevision);
        Assert.Equal("narrower-1", Assert.Single(current.Evidence).EvidenceId.Value);
    }

    [Fact]
    public async Task Shape_correct_locator_is_rejected_without_explicit_provider_ownership_verification()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var locatorSource = new CoordinatedCredentialCreateAdapter();
        var locatorVerifier = new RecordingRejectingLocatorVerifier();
        var provider = new CountingCreateCredentialValueProvider();
        var service = CredentialLifecyclePersistenceFactory.Create(paths, TestTrust(paths), locatorVerifier, provider, locatorSource, new CapabilityDependentIndex([locatorSource]), locatorSource, new AuditLog(paths));
        var reference = Reference() with { OwnerId = Environment.UserName };
        var binding = Binding() with { Scope = Binding().Scope with { ActorId = Environment.UserName } };

        var result = await service.ExecuteAsync(new CredentialLifecycleRequest(CredentialLifecycleOperationKind.Create, Id("reject-unowned-locator"), ReferenceId(), "workspace-1", Environment.UserName, 0, new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero), 4, reference, binding, Id("reject-unowned-locator-consent")), destination =>
        {
            destination.Fill(1);
            return destination.Length;
        });
        var persisted = await Store(paths).ReadAsync();

        Assert.Equal(CredentialLifecycleResultStatus.NeedsRepair, result.Status);
        Assert.Equal(Locator().Value, Assert.Single(locatorVerifier.Locators));
        Assert.Empty(persisted.Entries);
        Assert.Empty(JsonNode.Parse(await File.ReadAllTextAsync(paths.CredentialRegistryPrivateDocumentPath))!["locators"]!.AsArray());
        Assert.Equal(0, provider.CreateCount);
    }

    [Fact]
    public async Task Candidate_durability_failure_recovers_only_the_previously_proved_snapshot()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var seeded = await SeedRegistrationAsync(paths);
        var adapter = new CoordinatedCredentialCreateAdapter();
        var service = CredentialLifecyclePersistenceFactory.CreateWithPersistenceOptions(paths, TestTrust(paths), adapter, new CountingCreateCredentialValueProvider(), adapter, new CapabilityDependentIndex([adapter]), adapter, new AuditLog(paths), null, new FailOnDurabilityCallBarrier(1), null);

        var failed = await service.ExecuteAsync(new CredentialLifecycleRequest(CredentialLifecycleOperationKind.Test, Id("health-1"), ReferenceId(), "workspace-1", Environment.UserName, seeded.RegistryRevision!.Value, new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero)));
        Assert.Equal(CredentialLifecycleResultStatus.Unavailable, failed.Status);

        var recovered = await Store(paths).ReadAsync();
        Assert.True(recovered.Succeeded);
        Assert.Equal(seeded.RegistryRevision, recovered.RegistryRevision);
        Assert.Equal(CredentialProviderHealthStatus.Available, Assert.Single(recovered.Entries).Health);

        var retryAdapter = new CoordinatedCredentialCreateAdapter();
        var retry = CredentialLifecyclePersistenceFactory.Create(paths, TestTrust(paths), retryAdapter, new CountingCreateCredentialValueProvider(), retryAdapter, new CapabilityDependentIndex([retryAdapter]), retryAdapter, new AuditLog(paths));
        Assert.Equal(CredentialLifecycleResultStatus.Applied, (await retry.ExecuteAsync(new CredentialLifecycleRequest(CredentialLifecycleOperationKind.Test, Id("health-2"), ReferenceId(), "workspace-1", Environment.UserName, recovered.RegistryRevision!.Value, new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero)))).Status);
    }

    [Fact]
    public async Task Trust_anchor_advance_failure_never_acknowledges_an_untrusted_successor()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var seeded = await SeedRegistrationAsync(paths);
        var trust = new FailingCapabilityCatalogTrustProvider(TestTrust(paths));
        var store = new CredentialRegistryStore(paths, trust, new AcceptingLocatorVerifier());
        trust.FailNextAdvance = true;

        var failed = await store.AppendAsync(Evidence(Binding() with { Scope = Binding().Scope with { ActorId = Environment.UserName } }), default);
        Assert.False(failed.Succeeded);
        Assert.Equal(CredentialFailureCode.Unavailable, failed.Failure!.Code);
        var recovered = await Store(paths).ReadAsync();
        Assert.True(recovered.Succeeded);
        Assert.Equal(seeded.RegistryRevision, recovered.RegistryRevision);
        Assert.Equal(CredentialProviderHealthStatus.Available, Assert.Single(recovered.Entries).Health);
        Assert.Empty(recovered.Evidence);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task Initial_artifact_write_failure_recovers_the_server_authenticated_empty_state_for_retry(int failingWrite)
    {
        using var workspace = new TestWorkspace();
        using var trustRoot = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new FileCapabilityCatalogTrustProvider(trustRoot.RootPath);
        var adapter = new CoordinatedCredentialCreateAdapter();
        var provider = new CountingCreateCredentialValueProvider();
        var interrupted = CredentialLifecyclePersistenceFactory.CreateWithPersistenceOptions(paths, trust, adapter, provider, adapter, new CapabilityDependentIndex([adapter]), adapter, new AuditLog(paths), null, new FailOnDurabilityCallBarrier(failingWrite), null);
        var reference = Reference() with { OwnerId = Environment.UserName };
        var binding = Binding() with { Scope = Binding().Scope with { ActorId = Environment.UserName } };
        var failed = await interrupted.ExecuteAsync(new CredentialLifecycleRequest(CredentialLifecycleOperationKind.Create, Id("initial-write"), ReferenceId(), "workspace-1", Environment.UserName, 0, new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero), 4, reference, binding, Id("initial-write-consent")), destination =>
        {
            destination.Fill(1);
            return destination.Length;
        });

        Assert.NotEqual(CredentialLifecycleResultStatus.Applied, failed.Status);
        var restarted = new CredentialRegistryStore(paths, trust, new AcceptingLocatorVerifier());
        var empty = await restarted.ReadAsync();
        Assert.True(empty.Succeeded);
        Assert.Equal(0, empty.RegistryRevision);
        Assert.Empty(empty.Entries);

        await SeedRegistrationAsync(paths, trustProvider: trust);
        var completed = await new CredentialRegistryStore(paths, trust, new AcceptingLocatorVerifier()).ReadAsync();
        Assert.True(completed.Succeeded);
        Assert.Equal(3, completed.RegistryRevision);
        Assert.Single(completed.Entries);
    }

    [Fact]
    public async Task Rehashed_state_digests_cannot_reuse_an_authenticated_public_content_digest_and_tag()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        await SeedRegistrationAsync(paths);
        var publicDocument = JsonNode.Parse(await File.ReadAllTextAsync(paths.CredentialRegistryDocumentPath))!.AsObject();
        var privateDocument = JsonNode.Parse(await File.ReadAllTextAsync(paths.CredentialRegistryPrivateDocumentPath))!.AsObject();
        publicDocument["entries"]!.AsArray()[0]!.AsObject()["health"] = (int)CredentialProviderHealthStatus.Corrupt;
        var stateDigest = ComputeStateDigest(publicDocument, privateDocument);
        publicDocument["stateDigest"] = stateDigest;
        privateDocument["stateDigest"] = stateDigest;
        var forgedPublic = publicDocument.ToJsonString(JsonOptions(writeIndented: true));
        var forgedPrivate = privateDocument.ToJsonString(JsonOptions(writeIndented: true));
        await File.WriteAllTextAsync(paths.CredentialRegistryDocumentPath, forgedPublic);
        await File.WriteAllTextAsync(paths.CredentialRegistryPrivateDocumentPath, forgedPrivate);
        await File.WriteAllTextAsync(paths.CredentialRegistryProofPath, forgedPublic);
        await File.WriteAllTextAsync(paths.CredentialRegistryPrivateProofPath, forgedPrivate);

        var read = await Store(paths).ReadAsync();

        Assert.False(read.Succeeded);
        Assert.Equal(CredentialFailureCode.Unavailable, read.Failure!.Code);
    }

    [Fact]
    public async Task External_lock_contention_honors_cancellation_without_changing_the_registry()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var seeded = await SeedRegistrationAsync(paths);
        var store = Store(paths);

        using var externalLock = new FileStream(paths.CredentialRegistryLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var service = ReconciliationService(paths);
        var request = new CredentialLifecycleRequest(CredentialLifecycleOperationKind.Test, Id("blocked-health"), ReferenceId(), "workspace-1", Environment.UserName, seeded.RegistryRevision!.Value, new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero));
        var blocked = await service.ExecuteAsync(request, cancellationToken: cancellation.Token);

        Assert.Equal(CredentialLifecycleResultStatus.Unavailable, blocked.Status);
        externalLock.Dispose();
        var current = await store.ReadAsync();
        Assert.True(current.Succeeded);
        Assert.Equal(seeded.RegistryRevision, current.RegistryRevision);
        Assert.Equal(CredentialProviderHealthStatus.Available, Assert.Single(current.Entries).Health);
    }

    [Fact]
    public async Task Same_physical_workspace_rollback_is_rejected_by_the_monotonic_trust_anchor()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = Store(paths);
        await SeedRegistrationAsync(paths);
        var oldPublic = await File.ReadAllBytesAsync(paths.CredentialRegistryDocumentPath);
        var oldPrivate = await File.ReadAllBytesAsync(paths.CredentialRegistryPrivateDocumentPath);
        var revision = (await store.ReadAsync()).RegistryRevision!.Value;
        var service = ReconciliationService(paths);
        var preview = await service.PreviewAsync(new CredentialLifecyclePreviewRequest(Id("rollback-disable"), CredentialLifecycleOperationKind.Disable, ReferenceId(), "workspace-1", Environment.UserName, revision));
        var disable = new CredentialLifecycleRequest(CredentialLifecycleOperationKind.Disable, Id("rollback-disable"), ReferenceId(), "workspace-1", Environment.UserName, revision, new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero), Preview: preview, Confirmed: true);
        Assert.Equal(CredentialLifecycleResultStatus.Applied, (await service.ExecuteAsync(disable)).Status);

        await File.WriteAllBytesAsync(paths.CredentialRegistryDocumentPath, oldPublic);
        await File.WriteAllBytesAsync(paths.CredentialRegistryPrivateDocumentPath, oldPrivate);
        await File.WriteAllBytesAsync(paths.CredentialRegistryProofPath, oldPublic);
        await File.WriteAllBytesAsync(paths.CredentialRegistryPrivateProofPath, oldPrivate);

        var read = await Store(paths).ReadAsync();
        Assert.False(read.Succeeded);
        Assert.Equal(CredentialFailureCode.Unavailable, read.Failure!.Code);
    }

    [Fact]
    public async Task Matching_operation_replay_retains_the_immutable_original_receipt_after_later_tombstone()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var original = await SeedRegistrationAsync(paths);
        var store = Store(paths);
        var service = ReconciliationService(paths);
        var preview = await service.PreviewAsync(new CredentialLifecyclePreviewRequest(Id("tombstone-1"), CredentialLifecycleOperationKind.Delete, ReferenceId(), "workspace-1", Environment.UserName, original.RegistryRevision!.Value));
        var delete = new CredentialLifecycleRequest(CredentialLifecycleOperationKind.Delete, Id("tombstone-1"), ReferenceId(), "workspace-1", Environment.UserName, original.RegistryRevision.Value, new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero), Preview: preview, Confirmed: true);
        var deleted = await service.ExecuteAsync(delete);
        Assert.Equal(CredentialLifecycleResultStatus.Applied, deleted.Status);

        var adapter = new CoordinatedCredentialCreateAdapter();
        var provider = new CountingCreateCredentialValueProvider();
        var replayService = CredentialLifecyclePersistenceFactory.Create(paths, TestTrust(paths), adapter, provider, adapter, new CapabilityDependentIndex([adapter]), adapter, new AuditLog(paths));
        var reference = Reference() with { OwnerId = Environment.UserName };
        var binding = Binding() with { Scope = Binding().Scope with { ActorId = Environment.UserName } };
        var replay = await replayService.ExecuteAsync(new CredentialLifecycleRequest(CredentialLifecycleOperationKind.Create, Id("seed-register-1"), ReferenceId(), "workspace-1", Environment.UserName, 0, new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero), 4, reference, binding, Id("seed-consent-1")), destination =>
        {
            destination.Fill(1);
            return destination.Length;
        });

        Assert.Equal(CredentialLifecycleResultStatus.Replayed, replay.Status);
        Assert.Equal(deleted.RegistryRevision, replay.RegistryRevision);
        Assert.Equal(CredentialProviderHealthStatus.Missing, replay.Health);
        Assert.Equal(0, adapter.CreateCount);
        Assert.Equal(0, provider.CreateCount);
        Assert.Equal(CredentialProviderHealthStatus.Available, Assert.Single((await store.ReadAsync()).Operations, operation => operation.LifecycleIntentOperationId?.Value == "seed-register-1" && operation.LifecyclePhase == CredentialLifecycleMutationPhase.Complete).ResultHealth);
    }

    [Fact]
    public async Task Cancellation_while_trust_is_unavailable_does_not_poison_authenticated_state()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var seeded = await SeedRegistrationAsync(paths);
        var trust = new BlockingCapabilityCatalogTrustProvider(TestTrust(paths));
        var store = new CredentialRegistryStore(paths, trust, new AcceptingLocatorVerifier());

        trust.BlockNextRead = true;
        using var cancellation = new CancellationTokenSource();
        var pending = store.ReadAsync(cancellation.Token);
        await trust.Entered;
        cancellation.Cancel();
        var cancelled = await pending;
        trust.Release();

        Assert.False(cancelled.Succeeded);
        Assert.Equal(CredentialFailureCode.Unavailable, cancelled.Failure!.Code);
        var current = await Store(paths).ReadAsync();
        Assert.True(current.Succeeded);
        Assert.Equal(seeded.RegistryRevision, current.RegistryRevision);
        Assert.Equal(CredentialProviderHealthStatus.Available, Assert.Single(current.Entries).Health);
    }

    [Fact]
    public async Task Replaced_registry_directory_reparse_point_is_rejected_without_writing_outside_the_workspace()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        using var outside = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var seeded = await SeedRegistrationAsync(paths);

        Directory.Delete(paths.CredentialRegistryPath, recursive: true);
        Directory.CreateSymbolicLink(paths.CredentialRegistryPath, outside.RootPath);

        var read = await Store(paths).ReadAsync();
        var service = ReconciliationService(paths);
        var request = new CredentialLifecycleRequest(CredentialLifecycleOperationKind.Delete, Id("unsafe-delete"), ReferenceId(), "workspace-1", Environment.UserName, seeded.RegistryRevision!.Value, new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero), Confirmed: true);
        var mutation = await service.ExecuteAsync(request);
        Assert.False(read.Succeeded);
        Assert.Equal(CredentialFailureCode.Unavailable, read.Failure!.Code);
        Assert.Equal(CredentialLifecycleResultStatus.Unavailable, mutation.Status);
        Assert.Empty(Directory.EnumerateFiles(outside.RootPath, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Submitted_locator_canary_crosses_only_the_verifier_and_private_artifact_boundary()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var locatorCanary = Locator("loc_c0dec0dec0dec0dec0dec0dec0dec0dec0dec0dec0dec0dec0dec0dec0dec0de");

        var locator = new RecordingCredentialLifecycleLocator(locatorCanary);
        var provider = new CountingCreateCredentialValueProvider();
        var service = CredentialLifecyclePersistenceFactory.Create(paths, TestTrust(paths), locator, provider, locator, new CapabilityDependentIndex([new StubCapabilityDependentIndexSource()]), CredentialLifecyclePersistenceTestAdapter.Instance, new AuditLog(paths));
        var reference = Reference() with { OwnerId = Environment.UserName };
        var binding = Binding() with { Scope = Binding().Scope with { ActorId = Environment.UserName } };
        var registered = await service.ExecuteAsync(new CredentialLifecycleRequest(CredentialLifecycleOperationKind.Create, Id("locator-canary"), ReferenceId(), "workspace-1", Environment.UserName, 0, new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero), 4, reference, binding, Id("locator-canary-consent")), destination =>
        {
            destination.Fill(1);
            return destination.Length;
        });
        Assert.Equal(CredentialLifecycleResultStatus.Applied, registered.Status);
        Assert.NotEmpty(locator.VerifiedLocators);
        Assert.All(locator.VerifiedLocators, value => Assert.Equal(locatorCanary.Value, value));

        var publicArtifacts = new[] { await File.ReadAllTextAsync(paths.CredentialRegistryDocumentPath), await File.ReadAllTextAsync(paths.CredentialRegistryProofPath) };
        var privateArtifacts = new[] { await File.ReadAllTextAsync(paths.CredentialRegistryPrivateDocumentPath), await File.ReadAllTextAsync(paths.CredentialRegistryPrivateProofPath) };
        Assert.All(publicArtifacts, artifact => Assert.DoesNotContain(locatorCanary.Value, artifact, StringComparison.Ordinal));
        Assert.All(privateArtifacts, artifact => Assert.Contains(locatorCanary.Value, artifact, StringComparison.Ordinal));
        Assert.DoesNotContain(locatorCanary.Value, JsonSerializer.Serialize(registered), StringComparison.Ordinal);
        Assert.DoesNotContain(locatorCanary.Value, JsonSerializer.Serialize(await Store(paths).ReadAsync()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Entry_quota_is_preflighted_without_recording_the_rejected_operation()
    {
        using var workspace = new TestWorkspace();
        var quota = new CredentialRegistryQuota(2, 10, 10, 4, 128 * 1024);
        var paths = new WorkspacePaths(workspace.RootPath);
        var first = await SeedRegistrationAsync(paths, 1, quota: quota);
        var second = await SeedRegistrationAsync(paths, 2, expectedRegistryRevision: first.RegistryRevision!.Value, quota: quota);

        var adapter = new CoordinatedCredentialCreateAdapter();
        var provider = new CountingCreateCredentialValueProvider();
        var service = CredentialLifecyclePersistenceFactory.CreateWithPersistenceOptions(paths, TestTrust(paths), adapter, provider, adapter, new CapabilityDependentIndex([adapter]), adapter, new AuditLog(paths), null, null, quota);
        var referenceId = ReferenceId(3);
        var reference = Reference(referenceId) with { OwnerId = Environment.UserName };
        var binding = Binding(referenceId) with { Scope = Binding(referenceId).Scope with { ActorId = Environment.UserName } };
        var rejected = await service.ExecuteAsync(new CredentialLifecycleRequest(CredentialLifecycleOperationKind.Create, Id("seed-register-3"), referenceId, "workspace-1", Environment.UserName, second.RegistryRevision!.Value, new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero), 4, reference, binding, Id("seed-consent-3")), destination =>
        {
            destination.Fill(1);
            return destination.Length;
        });
        Assert.Equal(CredentialLifecycleResultStatus.Unavailable, rejected.Status);
        Assert.Equal(CredentialFailureCode.LimitExceeded, rejected.Failure!.Code);
        var current = await Store(paths).ReadAsync();
        Assert.True(current.Succeeded);
        Assert.Equal(quota.MaximumEntries, current.Entries.Count);
        Assert.Equal(second.RegistryRevision, current.RegistryRevision);
        Assert.DoesNotContain(current.Operations, operation => operation.OperationId.Value == "seed-register-3");
    }

    [Fact]
    public async Task Artifact_byte_quota_is_preflighted_before_any_registry_artifact_is_written()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var quota = new CredentialRegistryQuota(2, 2, 4, 4, 1024);
        var adapter = new CoordinatedCredentialCreateAdapter();
        var provider = new CountingCreateCredentialValueProvider();
        var service = CredentialLifecyclePersistenceFactory.CreateWithPersistenceOptions(paths, TestTrust(paths), adapter, provider, adapter, new CapabilityDependentIndex([adapter]), adapter, new AuditLog(paths), null, null, quota);
        var reference = Reference() with { OwnerId = Environment.UserName };
        var binding = Binding() with { Scope = Binding().Scope with { ActorId = Environment.UserName } };
        var rejected = await service.ExecuteAsync(new CredentialLifecycleRequest(CredentialLifecycleOperationKind.Create, Id("quota-create"), ReferenceId(), "workspace-1", Environment.UserName, 0, new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero), 4, reference, binding, Id("quota-consent")), destination =>
        {
            destination.Fill(1);
            return destination.Length;
        });
        Assert.Equal(CredentialLifecycleResultStatus.Unavailable, rejected.Status);
        Assert.False(File.Exists(paths.CredentialRegistryDocumentPath));
        Assert.False(File.Exists(paths.CredentialRegistryPrivateDocumentPath));
        Assert.False(File.Exists(paths.CredentialRegistryProofPath));
        Assert.False(File.Exists(paths.CredentialRegistryPrivateProofPath));

        await SeedRegistrationAsync(paths);
        Assert.True((await Store(paths).ReadAsync()).Succeeded);
    }

    [Fact]
    public void Constructor_rejects_invalid_trust_and_quota_bounds()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);

        Assert.Throws<ArgumentOutOfRangeException>(() => new CredentialRegistryStore(paths, new LongAuthenticationTagTrustProvider(TestTrust(paths), 0), new AcceptingLocatorVerifier()));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CredentialRegistryStore(paths, TestTrust(paths), new AcceptingLocatorVerifier(), quota: new CredentialRegistryQuota(0, 1, 1, 1, 1)));
    }

    [Fact]
    public async Task Public_operations_fail_closed_for_precancelled_and_unsafe_lock_state()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = Store(paths);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.False((await store.ReadAsync(cancelled.Token)).Succeeded);
        Assert.Equal(CredentialFailureCode.Unavailable, (await store.GetAsync(ReferenceId(), cancelled.Token)).Failure!.Code);
        var deniedMutation = await store.MutateAsync(Register(0), cancelled.Token);
        Assert.Equal(CredentialRegistryMutationStatus.Invalid, deniedMutation.Status);
        Assert.Equal(CredentialFailureCode.Unauthorized, deniedMutation.Failure!.Code);
        Assert.False(await store.AcknowledgeAuditAsync(Id("cancelled-audit"), cancelled.Token));
        Assert.Equal(CredentialFailureCode.Unavailable, (await store.AppendAsync(Evidence(Binding()), cancelled.Token)).Failure!.Code);

        Directory.CreateDirectory(paths.CredentialRegistryLockPath);
        var unsafeStore = Store(paths);
        Assert.False(await unsafeStore.AcknowledgeAuditAsync(Id("unsafe-lock-audit")));
        Assert.Equal(CredentialFailureCode.Unavailable, (await unsafeStore.AppendAsync(Evidence(Binding()), default)).Failure!.Code);
    }

    [Fact]
    public async Task Default_public_store_uses_platform_trust_or_fails_closed_when_unavailable()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);

        var read = await new CredentialRegistryStore(paths).ReadAsync();

        if (read.Succeeded)
        {
            Assert.True(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS());
            Assert.Empty(read.Entries);
            return;
        }

        Assert.False(read.Succeeded);
        Assert.Equal(CredentialFailureCode.Unavailable, read.Failure!.Code);
        Assert.False(File.Exists(paths.CredentialRegistryDocumentPath));
    }

    [Fact]
    public async Task Invalid_mutation_shapes_fail_before_storage_access()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = Store(paths);
        var invalid = new[]
        {
            await store.MutateAsync(null!),
            await store.MutateAsync(new CredentialRegistryMutation((CredentialRegistryMutationKind)999, Id("invalid-kind"), 0, ReferenceId(), null, null, null, null, null)),
            await store.MutateAsync(Register(0) with { ReferenceId = ReferenceId(2) }),
            await store.MutateAsync(new CredentialRegistryMutation(CredentialRegistryMutationKind.SetHealth, Id("invalid-health"), 0, ReferenceId(), Reference(), null, null, CredentialProviderHealthStatus.Available, null)),
            await store.MutateAsync(new CredentialRegistryMutation(CredentialRegistryMutationKind.Tombstone, Id("invalid-tombstone"), 0, ReferenceId(), null, null, null, CredentialProviderHealthStatus.Available, null)),
            await store.MutateAsync(new CredentialRegistryMutation(CredentialRegistryMutationKind.Bind, Id("invalid-bind"), 0, ReferenceId(), null, null, null, null, null)),
            await store.MutateAsync(new CredentialRegistryMutation(CredentialRegistryMutationKind.Consent, Id("invalid-consent"), 0, ReferenceId(), null, null, null, null, null)),
            await store.MutateAsync(new CredentialRegistryMutation(CredentialRegistryMutationKind.UpdatePosture, Id("invalid-posture"), 0, ReferenceId(), Reference(), null, null, null, null)),
            await store.MutateAsync(Register(0) with { OperationId = Id("invalid-active-runs"), AffectedActiveRuns = ["z", "a"] }),
            await store.MutateAsync(new CredentialRegistryMutation(CredentialRegistryMutationKind.RecordLocatorUncertain, Id("invalid-locator-uncertain"), 0, ReferenceId(), Reference(), null, null, null, null)),
            await store.MutateAsync(Register(0) with { OperationId = Id("invalid-lifecycle-shape"), LifecycleOperation = (int)CredentialLifecycleOperationKind.Create, LifecyclePhase = CredentialLifecycleMutationPhase.LocatorPrepared, LifecycleIntentOperationId = Id("invalid-lifecycle-intent") }),
            await store.MutateAsync(Register(0) with { OperationId = Id("invalid-audit-shape"), LifecycleAudit = IntentAuditPayload() })
        };

        Assert.Equal(CredentialFailureCode.InvalidRequest, invalid[0].Failure!.Code);
        Assert.All(invalid.Skip(1), result =>
        {
            Assert.Equal(CredentialRegistryMutationStatus.Invalid, result.Status);
            Assert.Equal(CredentialFailureCode.Unauthorized, result.Failure!.Code);
        });
        Assert.False(File.Exists(paths.CredentialRegistryDocumentPath));
    }

    [Fact]
    public async Task BindAndPostureCannotChangeImmutableProviderOrReferenceMetadata()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var seeded = await SeedRegistrationAsync(paths);
        var store = Store(paths);
        var seededRevision = seeded.RegistryRevision!.Value;
        Assert.True(CapabilityProviderId.TryParse("org.other", out var foreignProvider, out _));
        var foreignImplementation = Binding().Implementation with { ProviderId = foreignProvider! };
        var foreignBinding = Binding() with { Implementation = foreignImplementation, Scope = Binding().Scope with { Implementation = foreignImplementation } };
        var bind = new CredentialRegistryMutation(CredentialRegistryMutationKind.Bind, Id("bind-immutable"), seededRevision, ReferenceId(), null, foreignBinding, null, null, null);
        var changedReference = Reference() with { Purpose = "Changed outside the lifecycle posture fields.", Status = CredentialLifecycleStatus.Disabled };
        var posture = new CredentialRegistryMutation(CredentialRegistryMutationKind.UpdatePosture, Id("posture-immutable"), seededRevision, ReferenceId(), changedReference, null, null, CredentialProviderHealthStatus.Disabled, null);

        Assert.Equal(CredentialFailureCode.Unauthorized, (await store.MutateAsync(bind)).Failure!.Code);
        Assert.Equal(CredentialFailureCode.Unauthorized, (await store.MutateAsync(posture)).Failure!.Code);
        Assert.Equal(seededRevision, (await store.ReadAsync()).RegistryRevision);
    }

    [Fact]
    public async Task Undefined_registration_health_is_rejected_without_poisoning_later_valid_registration()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = Store(paths);

        var invalid = await store.MutateAsync(Register(0) with { Health = (CredentialProviderHealthStatus)999 });

        Assert.Equal(CredentialRegistryMutationStatus.Invalid, invalid.Status);
        Assert.Equal(CredentialFailureCode.Unauthorized, invalid.Failure!.Code);
        Assert.False(File.Exists(paths.CredentialRegistryDocumentPath));
        Assert.False(File.Exists(paths.CredentialRegistryPrivateDocumentPath));
        Assert.False(File.Exists(paths.CredentialRegistryProofPath));
        Assert.False(File.Exists(paths.CredentialRegistryPrivateProofPath));

        var seeded = await SeedRegistrationAsync(paths);
        var read = await store.ReadAsync();
        Assert.True(read.Succeeded);
        Assert.Equal(seeded.RegistryRevision, read.RegistryRevision);
        Assert.Equal(CredentialProviderHealthStatus.Available, Assert.Single(read.Entries).Health);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Public_mutation_cannot_reach_caller_supplied_authentication_tag_provider(bool oversized)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var tag = oversized ? new string('a', 65) : string.Empty;
        var trust = new InvalidAuthenticationTagTrustProvider(TestTrust(paths), tag);
        var result = await new CredentialRegistryStore(paths, trust, new AcceptingLocatorVerifier()).MutateAsync(Register(0));

        Assert.Equal(CredentialRegistryMutationStatus.Invalid, result.Status);
        Assert.Equal(CredentialFailureCode.Unauthorized, result.Failure!.Code);
        Assert.False(File.Exists(paths.CredentialRegistryDocumentPath));
        Assert.False(File.Exists(paths.CredentialRegistryPrivateDocumentPath));
    }

    [Fact]
    public async Task Lookup_and_evidence_replay_conflict_and_quota_results_are_explicit()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var quota = new CredentialRegistryQuota(2, 2, 4, 1, 128 * 1024);
        var store = new CredentialRegistryStore(paths, TestTrust(paths), new AcceptingLocatorVerifier(), quota: quota);
        var missing = await store.GetAsync(ReferenceId(), default);
        Assert.False(missing.Succeeded);
        Assert.Equal(CredentialFailureCode.NotFound, missing.Failure!.Code);

        await SeedRegistrationAsync(paths, quota: quota);
        var found = await store.GetAsync(ReferenceId(), default);
        Assert.True(found.Succeeded);
        Assert.Equal(ReferenceId(), found.Reference!.Id);

        var binding = Binding() with { Scope = Binding().Scope with { ActorId = Environment.UserName } };
        var invalidEvidence = Evidence(binding, "invalid-evidence") with { ReferenceId = null! };
        Assert.Equal(CredentialFailureCode.InvalidRequest, (await store.AppendAsync(invalidEvidence, default)).Failure!.Code);

        var evidence = Evidence(binding);
        Assert.True((await store.AppendAsync(evidence, default)).Succeeded);
        Assert.True((await store.AppendAsync(evidence, default)).Succeeded);
        var changedReplay = evidence with { UsedAtUtc = evidence.UsedAtUtc.AddMinutes(1) };
        Assert.Equal(CredentialFailureCode.Conflict, (await store.AppendAsync(changedReplay, default)).Failure!.Code);
        var wrongBinding = Evidence(binding, "wrong-binding") with { BindingHash = CredentialContractHash.Compute("forged") };
        Assert.Equal(CredentialFailureCode.Conflict, (await store.AppendAsync(wrongBinding, default)).Failure!.Code);
        Assert.Equal(CredentialFailureCode.LimitExceeded, (await store.AppendAsync(Evidence(binding, "over-limit"), default)).Failure!.Code);
    }

    private static CredentialRegistryMutation Register(long revision)
    {
        return Register(1, revision);
    }

    private static CredentialRegistryMutation Register(int index, long revision, CredentialProviderLocator? locator = null)
    {
        var referenceId = ReferenceId(index);
        var reference = Reference(referenceId);
        var binding = Binding(referenceId);
        Assert.True(CredentialContractJson.TrySerialize(reference, out _, out var referenceValidation), string.Join(';', referenceValidation.Errors.Select(error => error.Message)));
        Assert.True(CredentialContractJson.TrySerialize(binding, out _, out var bindingValidation), string.Join(';', bindingValidation.Errors.Select(error => error.Message)));
        return new CredentialRegistryMutation(CredentialRegistryMutationKind.Register, Id($"register-{index}"), revision, referenceId, reference, binding, Id("consent-1"), CredentialProviderHealthStatus.Available, locator ?? Locator());
    }

    private static CredentialRegistryStore Store(WorkspacePaths paths, TimeProvider? timeProvider = null) => new(paths, TestTrust(paths), new AcceptingLocatorVerifier(), timeProvider);

    private static async Task<CredentialRegistryReadResult> SeedRegistrationAsync(WorkspacePaths paths, int index = 1, IAuditLog? auditLog = null, long expectedRegistryRevision = 0, CredentialRegistryQuota? quota = null, FileCapabilityCatalogTrustProvider? trustProvider = null)
    {
        var adapter = new CoordinatedCredentialCreateAdapter();
        var provider = new CountingCreateCredentialValueProvider();
        var dependentIndex = new CapabilityDependentIndex([adapter]);
        var service = CredentialLifecyclePersistenceFactory.CreateWithPersistenceOptions(paths, trustProvider ?? TestTrust(paths), adapter, provider, adapter, dependentIndex, adapter, auditLog ?? new AuditLog(paths), null, null, quota);
        var referenceId = ReferenceId(index);
        var reference = Reference(referenceId) with { OwnerId = Environment.UserName };
        var binding = Binding(referenceId) with { Scope = Binding(referenceId).Scope with { ActorId = Environment.UserName } };
        var request = new CredentialLifecycleRequest(
            CredentialLifecycleOperationKind.Create,
            Id($"seed-register-{index}"),
            referenceId,
            "workspace-1",
            Environment.UserName,
            expectedRegistryRevision,
            new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero),
            4,
            reference,
            binding,
            Id($"seed-consent-{index}"));

        var result = await service.ExecuteAsync(request, destination =>
        {
            destination.Fill(1);
            return destination.Length;
        });

        Assert.Equal(CredentialLifecycleResultStatus.Applied, result.Status);
        Assert.Equal(1, adapter.CreateCount);
        Assert.Equal(1, provider.CreateCount);
        var read = await new CredentialRegistryStore(paths, trustProvider ?? TestTrust(paths), adapter).ReadAsync();
        Assert.True(read.Succeeded);
        Assert.Equal(result.RegistryRevision, read.RegistryRevision);
        return read;
    }

    private static async Task<CredentialRegistryReadResult> SeedPreparedRegistrationAsync(WorkspacePaths paths)
    {
        var locatorMarker = Path.Combine(paths.WorkspacePath, "prepared-locator.marker");
        var providerEntryMarker = Path.Combine(paths.WorkspacePath, "prepared-provider-entered.marker");
        var reference = Reference() with { OwnerId = Environment.UserName };
        var binding = Binding() with { Scope = Binding().Scope with { ActorId = Environment.UserName } };
        Assert.True(CredentialContractJson.TrySerialize(reference, out var referenceJson, out _));
        Assert.True(CredentialContractJson.TrySerialize(binding, out var bindingJson, out _));
        using var crashHost = StartCredentialPayloadCreateCrashHost(paths.WorkspacePath, "registry", "prepared-create", "prepared-consent", referenceJson!, bindingJson!, locatorMarker, providerEntryMarker);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!File.Exists(providerEntryMarker))
            {
                await Task.Delay(25, timeout.Token);
            }
            Assert.True(File.Exists(locatorMarker));
            var prepared = await Store(paths).ReadAsync();
            Assert.Equal(2, prepared.RegistryRevision);
            Assert.Equal([CredentialLifecycleMutationPhase.Intent, CredentialLifecycleMutationPhase.LocatorPrepared], prepared.Operations.Select(operation => operation.LifecyclePhase).ToArray());
            return prepared;
        }
        finally
        {
            if (!crashHost.HasExited)
            {
                crashHost.Kill(entireProcessTree: true);
            }
            await crashHost.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static Process StartCredentialPayloadCreateCrashHost(string workspaceRoot, string trustProfile, string operationId, string consentId, string referenceJson, string bindingJson, string locatorMarker, string providerEntryMarker)
    {
        var outputDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        var targetFramework = outputDirectory.Name;
        var configuration = outputDirectory.Parent?.Name ?? throw new DirectoryNotFoundException("The active test build configuration could not be resolved.");
        var hostAssembly = Path.Combine(FindRepositoryRoot(), "tests", "EmbodySense.CancellationHost", "bin", configuration, targetFramework, "EmbodySense.CancellationHost.dll");
        Assert.True(File.Exists(hostAssembly), $"Cancellation host assembly was not built at `{hostAssembly}`.");
        var startInfo = new ProcessStartInfo("dotnet") { UseShellExecute = false };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(hostAssembly);
        startInfo.ArgumentList.Add("credential-create-payload-crash");
        startInfo.ArgumentList.Add(workspaceRoot);
        startInfo.ArgumentList.Add(trustProfile);
        startInfo.ArgumentList.Add(operationId);
        startInfo.ArgumentList.Add(consentId);
        startInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.UTF8.GetBytes(referenceJson)));
        startInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.UTF8.GetBytes(bindingJson)));
        startInfo.ArgumentList.Add(locatorMarker);
        startInfo.ArgumentList.Add(providerEntryMarker);
        startInfo.Environment["DOTNET_ROLL_FORWARD"] = "Major";
        return Process.Start(startInfo) ?? throw new InvalidOperationException("The credential prepared-create crash process could not be started.");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EmbodySense.sln")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new DirectoryNotFoundException("The repository root could not be located from the test output directory.");
    }

    private static CredentialLifecycleService ReconciliationService(WorkspacePaths paths, bool deleteSucceeds = true)
    {
        var dependentIndex = new CapabilityDependentIndex([new StubCapabilityDependentIndexSource()]);
        return CredentialLifecyclePersistenceFactory.Create(paths, TestTrust(paths), CredentialLifecyclePersistenceTestAdapter.Instance, new CountingCredentialValueProvider(new StrongBox<int>(), deleteSucceeds), CredentialLifecyclePersistenceTestAdapter.Instance, dependentIndex, CredentialLifecyclePersistenceTestAdapter.Instance, new AuditLog(paths));
    }

    private static FileCapabilityCatalogTrustProvider TestTrust(WorkspacePaths paths)
    {
        var workspaceRoot = new DirectoryInfo(paths.WorkspacePath);
        var temporaryRoot = workspaceRoot.Parent?.Parent ?? throw new InvalidOperationException("The test workspace root is invalid.");
        return new FileCapabilityCatalogTrustProvider(Path.Combine(temporaryRoot.FullName, "embodysense-test-server-state", workspaceRoot.Name, "credential-registry-trust"));
    }

    private static string ComputeStateDigest(JsonObject publicDocument, JsonObject privateDocument)
    {
        var publicState = publicDocument.DeepClone().AsObject();
        var privateState = privateDocument.DeepClone().AsObject();
        publicState["stateDigest"] = string.Empty;
        publicState["contentDigest"] = string.Empty;
        publicState["authenticationTag"] = string.Empty;
        privateState["stateDigest"] = string.Empty;
        var content = publicState.ToJsonString(JsonOptions(writeIndented: false)) + "\n" + privateState.ToJsonString(JsonOptions(writeIndented: false));
        return "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
    }

    private static string ComputeContentDigest(JsonObject publicDocument)
    {
        var contentDocument = publicDocument.DeepClone().AsObject();
        contentDocument["contentDigest"] = string.Empty;
        contentDocument["authenticationTag"] = string.Empty;
        return "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(contentDocument.ToJsonString(JsonOptions(writeIndented: false))))).ToLowerInvariant();
    }

    private static JsonSerializerOptions JsonOptions(bool writeIndented) => new(JsonSerializerDefaults.Web) { WriteIndented = writeIndented };
    private static string Hash(char value) => "sha256:" + new string(value, 64);

    private static void CorruptRegistry(JsonObject publicNode, JsonObject privateNode, string corruption)
    {
        var entries = publicNode["entries"]!.AsArray();
        var operations = publicNode["operations"]!.AsArray();
        var locators = privateNode["locators"]!.AsArray();
        switch (corruption)
        {
            case "entry-health":
                entries[0]!["health"] = 999;
                break;
            case "entry-order":
                (entries[0], entries[1]) = (entries[1]!.DeepClone(), entries[0]!.DeepClone());
                break;
            case "locator-order":
                (locators[0], locators[1]) = (locators[1]!.DeepClone(), locators[0]!.DeepClone());
                break;
            case "duplicate-locator":
                locators.Add(locators[0]!.DeepClone());
                break;
            case "operation-id":
                operations[0]!["operationId"] = string.Empty;
                break;
            case "duplicate-operation":
                operations.Add(operations[0]!.DeepClone());
                break;
            case "unexpected-audit-outbox":
                operations[0]!["auditOutbox"] = new JsonObject
                {
                    ["occurredAtUtc"] = "2026-08-02T12:00:00+00:00",
                    ["registryRevision"] = operations[0]!["revision"]!.GetValue<long>(),
                    ["action"] = "unexpected",
                    ["outcome"] = "unexpected",
                    ["detail"] = "Unexpected unaudited outbox."
                };
                break;
            case "invalid-audit-delivery":
                publicNode["auditDeliveries"]!.AsArray().Add(new JsonObject { ["terminalOperationId"] = string.Empty, ["deliveredAtUtc"] = "2026-08-02T12:00:00+00:00" });
                break;
            case "invalid-evidence":
                publicNode["evidence"]!.AsArray().Add(new JsonObject { ["evidenceJson"] = "{}" });
                break;
            case "invalid-tombstone":
                publicNode["tombstones"]!.AsArray()[0]!["tombstonedAtUtc"] = "2026-08-02T07:00:00-05:00";
                break;
            case "invalid-lifecycle-shape":
                var operation = operations[0]!.AsObject();
                operation["lifecycleOperation"] = (int)CredentialLifecycleOperationKind.Create;
                operation["actorId"] = "user-1";
                operation["workspaceId"] = "workspace-1";
                operation["lifecycleRequestHash"] = Hash('c');
                operation["lifecyclePhase"] = (int)CredentialLifecycleMutationPhase.Intent;
                operation["lifecycleIntentOperationId"] = operation["operationId"]!.GetValue<string>();
                operation["auditOutbox"] = new JsonObject
                {
                    ["occurredAtUtc"] = "2026-08-02T12:00:00+00:00",
                    ["registryRevision"] = operation["revision"]!.GetValue<long>(),
                    ["action"] = AuditSchema.Actions.CredentialLifecycleIntent,
                    ["outcome"] = AuditSchema.Outcomes.Started,
                    ["detail"] = "Credential lifecycle intent durably recorded."
                };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(corruption));
        }
    }

    private sealed class AcceptingLocatorVerifier : ICredentialProviderLocatorVerifier
    {
        public ValueTask<bool> VerifyAsync(string workspaceIdentity, CredentialReferenceId referenceId, CredentialProviderId providerId, CredentialProviderLocator locator, CancellationToken cancellationToken) => ValueTask.FromResult(true);
    }

    private sealed class OversizedAuthenticationTagTrustProvider(ICapabilityCatalogTrustProvider inner) : ICapabilityCatalogTrustProvider
    {
        public int MaximumAuthenticationTagUtf8Bytes => inner.MaximumAuthenticationTagUtf8Bytes;

        public void RequireDisjointWorkspace(string workspaceRootPath) => inner.RequireDisjointWorkspace(workspaceRootPath);

        public Task<CapabilityCatalogTrustState?> ReadAsync(string workspaceIdentity, CancellationToken cancellationToken = default) => inner.ReadAsync(workspaceIdentity, cancellationToken);

        public Task<CapabilityCatalogTrustState> InitializeAsync(string workspaceIdentity, long generation, string contentDigest, CancellationToken cancellationToken = default) => inner.InitializeAsync(workspaceIdentity, generation, contentDigest, cancellationToken);

        public Task<string> AuthenticateArtifactAsync(string workspaceIdentity, long generation, string contentDigest, CancellationToken cancellationToken = default) => Task.FromResult(new string('x', MaximumAuthenticationTagUtf8Bytes + 1));

        public Task<bool> VerifyArtifactAsync(string workspaceIdentity, long generation, string contentDigest, string authenticationTag, CancellationToken cancellationToken = default) => inner.VerifyArtifactAsync(workspaceIdentity, generation, contentDigest, authenticationTag, cancellationToken);

        public Task<CapabilityCatalogTrustState> AdvanceAsync(string workspaceIdentity, long expectedGeneration, string expectedContentDigest, long newGeneration, string newContentDigest, CancellationToken cancellationToken = default) => inner.AdvanceAsync(workspaceIdentity, expectedGeneration, expectedContentDigest, newGeneration, newContentDigest, cancellationToken);
    }

    private sealed class RecordingLocatorVerifier : ICredentialProviderLocatorVerifier
    {
        public List<string> Locators { get; } = [];

        public ValueTask<bool> VerifyAsync(string workspaceIdentity, CredentialReferenceId referenceId, CredentialProviderId providerId, CredentialProviderLocator locator, CancellationToken cancellationToken)
        {
            Locators.Add(locator.Value);
            return ValueTask.FromResult(true);
        }
    }

    private sealed class RecordingRejectingLocatorVerifier : ICredentialProviderLocatorVerifier
    {
        internal List<string> Locators { get; } = [];

        public ValueTask<bool> VerifyAsync(string workspaceIdentity, CredentialReferenceId referenceId, CredentialProviderId providerId, CredentialProviderLocator locator, CancellationToken cancellationToken)
        {
            Locators.Add(locator.Value);
            return ValueTask.FromResult(false);
        }
    }

    private sealed class RecordingCredentialLifecycleLocator(CredentialProviderLocator locator) : ICredentialProviderLocatorSource, ICredentialProviderLocatorVerifier
    {
        internal List<string> VerifiedLocators { get; } = [];

        public ValueTask<CredentialProviderLocator?> CreateAsync(string workspaceId, CredentialReferenceId referenceId, CredentialProviderId providerId, CancellationToken cancellationToken) => ValueTask.FromResult<CredentialProviderLocator?>(locator);

        public ValueTask<bool> VerifyAsync(string workspaceIdentity, CredentialReferenceId referenceId, CredentialProviderId providerId, CredentialProviderLocator candidate, CancellationToken cancellationToken)
        {
            VerifiedLocators.Add(candidate.Value);
            return ValueTask.FromResult(true);
        }
    }

    private sealed class LongAuthenticationTagTrustProvider(ICapabilityCatalogTrustProvider inner, int maximumAuthenticationTagUtf8Bytes) : ICapabilityCatalogTrustProvider
    {
        public int MaximumAuthenticationTagUtf8Bytes { get; } = maximumAuthenticationTagUtf8Bytes;
        public int InitializeCount { get; private set; }
        public int AuthenticateCount { get; private set; }

        public void RequireDisjointWorkspace(string workspaceRootPath) => inner.RequireDisjointWorkspace(workspaceRootPath);

        public Task<CapabilityCatalogTrustState?> ReadAsync(string workspaceIdentity, CancellationToken cancellationToken = default) => inner.ReadAsync(workspaceIdentity, cancellationToken);

        public async Task<CapabilityCatalogTrustState> InitializeAsync(string workspaceIdentity, long generation, string contentDigest, CancellationToken cancellationToken = default)
        {
            InitializeCount++;
            return await inner.InitializeAsync(workspaceIdentity, generation, contentDigest, cancellationToken);
        }

        public Task<string> AuthenticateArtifactAsync(string workspaceIdentity, long generation, string contentDigest, CancellationToken cancellationToken = default)
        {
            AuthenticateCount++;
            return Task.FromResult(new string('a', MaximumAuthenticationTagUtf8Bytes));
        }

        public Task<bool> VerifyArtifactAsync(string workspaceIdentity, long generation, string contentDigest, string authenticationTag, CancellationToken cancellationToken = default) => inner.VerifyArtifactAsync(workspaceIdentity, generation, contentDigest, authenticationTag, cancellationToken);
        public Task<CapabilityCatalogTrustState> AdvanceAsync(string workspaceIdentity, long expectedGeneration, string expectedContentDigest, long newGeneration, string newContentDigest, CancellationToken cancellationToken = default) => inner.AdvanceAsync(workspaceIdentity, expectedGeneration, expectedContentDigest, newGeneration, newContentDigest, cancellationToken);
    }

    private sealed class InvalidAuthenticationTagTrustProvider(ICapabilityCatalogTrustProvider inner, string authenticationTag) : ICapabilityCatalogTrustProvider
    {
        public int MaximumAuthenticationTagUtf8Bytes => 64;
        public void RequireDisjointWorkspace(string workspaceRootPath) => inner.RequireDisjointWorkspace(workspaceRootPath);
        public Task<CapabilityCatalogTrustState?> ReadAsync(string workspaceIdentity, CancellationToken cancellationToken = default) => inner.ReadAsync(workspaceIdentity, cancellationToken);
        public Task<CapabilityCatalogTrustState> InitializeAsync(string workspaceIdentity, long generation, string contentDigest, CancellationToken cancellationToken = default) => inner.InitializeAsync(workspaceIdentity, generation, contentDigest, cancellationToken);
        public Task<string> AuthenticateArtifactAsync(string workspaceIdentity, long generation, string contentDigest, CancellationToken cancellationToken = default) => Task.FromResult(authenticationTag);
        public Task<bool> VerifyArtifactAsync(string workspaceIdentity, long generation, string contentDigest, string candidateTag, CancellationToken cancellationToken = default) => inner.VerifyArtifactAsync(workspaceIdentity, generation, contentDigest, candidateTag, cancellationToken);
        public Task<CapabilityCatalogTrustState> AdvanceAsync(string workspaceIdentity, long expectedGeneration, string expectedContentDigest, long newGeneration, string newContentDigest, CancellationToken cancellationToken = default) => inner.AdvanceAsync(workspaceIdentity, expectedGeneration, expectedContentDigest, newGeneration, newContentDigest, cancellationToken);
    }

    private sealed class FailOnDurabilityCallBarrier(int failingCall) : ICapabilityCatalogDurabilityBarrier
    {
        private int _callCount;

        public void BeforeDirectoryMove(string stagingPath, string destinationPath)
        {
        }

        public void AfterDirectoryMove(string stagingPath, string destinationPath)
        {
        }

        public void FlushAfterDirectoryCreate(string directoryPath, SafeFileHandle parentDirectory)
        {
        }

        public ValueTask FlushAfterRenameAsync(string destinationPath, SafeFileHandle parentDirectory)
        {
            if (Interlocked.Increment(ref _callCount) == failingCall)
            {
                throw new IOException("Injected credential-registry durability barrier failure.");
            }

            return ValueTask.CompletedTask;
        }
    }

    private static CredentialReference Reference(CredentialReferenceId? referenceId = null)
    {
        return new CredentialReference(1, referenceId ?? ReferenceId(), "api-token", CredentialLifecycleStatus.Active, "user-1", "Call the example service.", ProviderId("org.example"), new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero), null, new Dictionary<string, string> { ["service"] = "Example" });
    }

    private static CredentialCapabilityBinding Binding(CredentialReferenceId? referenceId = null)
    {
        var descriptor = CapabilityCatalogTestData.Descriptor();
        Assert.True(CapabilityDescriptorIdentity.TryCreate(descriptor, out var identity, out var validation), string.Join(';', validation.Errors.Select(error => error.Message)));
        _ = CapabilitySecretRequirement.TryParse("provider-token", out var requirement, out _);
        var scope = new CredentialScope("workspace-1", "role-1", "loop-1", 1, "node-1", identity, descriptor.Implementation, "example", "target", "read", "user-1", null, null);
        return new CredentialCapabilityBinding(1, referenceId ?? ReferenceId(), requirement!, identity!, descriptor.Implementation, scope);
    }

    private static CredentialUseEvidence Evidence(CredentialCapabilityBinding binding, string evidenceId = "evidence-1", CredentialScope? usedScope = null)
    {
        Assert.True(CredentialContractJson.TryHash(binding, out var hash, out _));
        return new CredentialUseEvidence(1, Id(evidenceId), binding.ReferenceId, hash!, Id("proof-1"), Id("run-1"), usedScope ?? binding.Scope, new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero), CredentialUseOutcome.Succeeded, true);
    }

    private static CredentialReferenceId ReferenceId()
    {
        return ReferenceId(1);
    }

    private static CredentialReferenceId ReferenceId(int index)
    {
        Assert.True(CredentialReferenceId.TryParse($"credential-{index}", out var value, out _));
        return value!;
    }

    private static CredentialProviderId ProviderId(string value)
    {
        Assert.True(CredentialProviderId.TryParse(value, out var parsed, out _));
        return parsed!;
    }

    private static CredentialContractId Id(string value)
    {
        Assert.True(CredentialContractId.TryParse(value, out var parsed, out _));
        return parsed!;
    }

    private static CredentialProviderLocator Locator(string value = "loc_0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")
    {
        Assert.True(CredentialProviderLocator.TryParse(value, out var parsed));
        return parsed!;
    }

    private static CredentialLifecycleAuditPayload AuditPayload(string outcome) => new(AuditSchema.Actions.CredentialLifecycleOutcome, outcome, "Credential lifecycle terminal outcome recorded.");

    private static CredentialLifecycleAuditPayload IntentAuditPayload() => new(AuditSchema.Actions.CredentialLifecycleIntent, AuditSchema.Outcomes.Started, "Credential lifecycle intent durably recorded.");

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class FailingAuditLog : IAuditLog
    {
        public Task AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default) => throw new IOException("Injected audit sink failure.");
        public Task<IReadOnlyList<AuditEvent>> ReadTailAsync(int limit, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AuditEvent>>([]);
    }

    private sealed class TerminalCreateCredentialValueProvider(CredentialLifecycleMutationPhase phase) : ICredentialValueProvider
    {
        internal int CreateCount { get; private set; }

        public ValueTask<CredentialProviderResult> CreateAsync(CredentialProviderMutationRequest request, CredentialSecretWriteCallback source, CancellationToken cancellationToken)
        {
            CreateCount++;
            if (phase == CredentialLifecycleMutationPhase.Rollback)
            {
                return ValueTask.FromResult(CredentialProviderResult.Failed(CredentialFailure.FromCode(CredentialFailureCode.InvalidRequest)));
            }
            if (phase == CredentialLifecycleMutationPhase.Uncertain)
            {
                return ValueTask.FromResult(CredentialProviderResult.Failed(CredentialFailure.FromCode(CredentialFailureCode.OutcomeUncertain)));
            }

            var destination = new byte[request.ValueByteLength];
            return ValueTask.FromResult(source(destination) == destination.Length ? CredentialProviderResult.Success() : CredentialProviderResult.Failed(CredentialFailure.FromCode(CredentialFailureCode.CallbackFailed)));
        }

        public ValueTask<CredentialProviderResult> ReplaceAsync(CredentialProviderMutationRequest request, CredentialSecretWriteCallback source, CancellationToken cancellationToken) => ValueTask.FromResult(CredentialProviderResult.Failed(CredentialFailure.FromCode(CredentialFailureCode.InvalidRequest)));
        public ValueTask<CredentialProviderResult> UseAsync(CredentialProviderUseRequest request, ICredentialTrustedUseConsumer trustedConsumer, CancellationToken cancellationToken) => ValueTask.FromResult(CredentialProviderResult.Failed(CredentialFailure.FromCode(CredentialFailureCode.InvalidRequest)));
        public ValueTask<CredentialProviderResult> DeleteAsync(CredentialProviderDeleteRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(CredentialProviderResult.Failed(CredentialFailure.FromCode(CredentialFailureCode.InvalidRequest)));
        public ValueTask<CredentialProviderHealthResult> GetHealthAsync(CredentialProviderUseRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(CredentialProviderHealthResult.Missing());
    }
}
