using EmbodySense.Core.Application.Credentials;
using EmbodySense.Core.Application.Credentials.Models;
using EmbodySense.Core.Application.Capabilities;
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
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EmbodySense.Core.Persistence.Tests.Credentials;

public sealed class CredentialRegistryStoreTests
{
    [Fact]
    public async Task Lifecycle_binding_consent_and_restrictive_posture_are_revisioned_and_restart_safe()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = Store(paths);
        Assert.Equal(CredentialRegistryMutationStatus.Applied, (await store.MutateAsync(Register(0))).Status);
        var rebound = Binding() with { Scope = Binding().Scope with { LoopRevision = 2 } };
        var bind = await store.MutateAsync(new CredentialRegistryMutation(CredentialRegistryMutationKind.Bind, Id("bind-1"), 1, ReferenceId(), null, rebound, null, null, null, null, (int)CredentialLifecycleOperationKind.Bind, "user-1"));
        var consent = await store.MutateAsync(new CredentialRegistryMutation(CredentialRegistryMutationKind.Consent, Id("consent-2"), 2, ReferenceId(), null, null, Id("consent-document-2"), null, null, true, (int)CredentialLifecycleOperationKind.Consent, "user-1"));
        var revokedReference = Reference() with { Status = CredentialLifecycleStatus.Revoked, UpdatedAtUtc = new DateTimeOffset(2026, 8, 1, 12, 1, 0, TimeSpan.Zero) };

        var revoked = await new CredentialRegistryStore(paths, TestTrust(paths), new RejectingCredentialProviderLocatorVerifier()).MutateAsync(new CredentialRegistryMutation(CredentialRegistryMutationKind.UpdatePosture, Id("revoke-1"), 3, ReferenceId(), revokedReference, null, null, CredentialProviderHealthStatus.Revoked, null, null, (int)CredentialLifecycleOperationKind.Revoke, "user-1", "sha256:" + new string('b', 64), null, null, null, ["run-1", "run-2"]));

        Assert.Equal(CredentialRegistryMutationStatus.Applied, bind.Status);
        Assert.Equal(CredentialRegistryMutationStatus.Applied, consent.Status);
        Assert.Equal(CredentialRegistryMutationStatus.Applied, revoked.Status);
        var entry = Assert.Single((await Store(paths).ReadAsync()).Entries);
        Assert.Equal(2, entry.Binding.Scope.LoopRevision);
        Assert.True(entry.ConsentGranted);
        Assert.Equal("consent-document-2", entry.ConsentReference.Value);
        Assert.Equal(CredentialLifecycleStatus.Revoked, entry.Reference.Status);
        Assert.Equal(CredentialProviderHealthStatus.Revoked, entry.Health);
        Assert.Equal(["run-1", "run-2"], Assert.Single((await Store(paths).ReadAsync()).Operations, operation => operation.OperationId.Value == "revoke-1").AffectedActiveRuns);
        var publicDocument = await File.ReadAllTextAsync(paths.CredentialRegistryDocumentPath);
        Assert.Contains("\"lifecycleShape\": 1", publicDocument, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProviderCompletionPhaseCannotBeReservedWithoutExactDurableIntent()
    {
        using var workspace = new TestWorkspace();
        var store = Store(new WorkspacePaths(workspace.RootPath));
        var intentId = Id("phase-intent");
        var completion = new CredentialRegistryMutation(CredentialRegistryMutationKind.SetHealth, Id("phase-complete"), 0, ReferenceId(), null, null, null, CredentialProviderHealthStatus.Available, null, LifecycleOperation: (int)CredentialLifecycleOperationKind.Create, ActorId: "user-1", LifecycleRequestHash: Hash('d'), LifecyclePhase: CredentialLifecycleMutationPhase.Complete, LifecycleIntentOperationId: intentId, WorkspaceId: "workspace-1", LifecycleAudit: AuditPayload("succeeded"));

        var result = await store.MutateAsync(completion);

        Assert.Equal(CredentialRegistryMutationStatus.Conflict, result.Status);
    }

    [Theory]
    [InlineData(CredentialLifecycleMutationPhase.Complete, CredentialProviderHealthStatus.Available, "succeeded")]
    [InlineData(CredentialLifecycleMutationPhase.Rollback, CredentialProviderHealthStatus.Missing, "failed")]
    [InlineData(CredentialLifecycleMutationPhase.Uncertain, CredentialProviderHealthStatus.NeedsRepair, "failed")]
    public async Task CorrelatedProviderTerminalPhasesPersistExactOutboxAndProjection(CredentialLifecycleMutationPhase phase, CredentialProviderHealthStatus health, string auditOutcome)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = Store(paths);
        var intentId = Id($"matrix-intent-{phase.ToString().ToLowerInvariant()}");
        var requestHash = Hash('6');
        var intent = new CredentialRegistryMutation(CredentialRegistryMutationKind.BeginCreate, intentId, 0, ReferenceId(), Reference(), Binding(), Id("consent-matrix"), CredentialProviderHealthStatus.NeedsRepair, null, false, (int)CredentialLifecycleOperationKind.Create, "user-1", null, requestHash, CredentialLifecycleMutationPhase.Intent, intentId, null, "workspace-1", IntentAuditPayload());
        Assert.Equal(CredentialRegistryMutationStatus.Applied, (await store.MutateAsync(intent)).Status);
        var locatorPrepared = new CredentialRegistryMutation(CredentialRegistryMutationKind.Register, Id($"matrix-locator-{phase.ToString().ToLowerInvariant()}"), 1, ReferenceId(), Reference(), Binding(), Id("consent-matrix"), CredentialProviderHealthStatus.NeedsRepair, Locator(), false, (int)CredentialLifecycleOperationKind.Create, "user-1", null, requestHash, CredentialLifecycleMutationPhase.LocatorPrepared, intentId, null, "workspace-1");
        Assert.Equal(CredentialRegistryMutationStatus.Applied, (await store.MutateAsync(locatorPrepared)).Status);
        var terminal = new CredentialRegistryMutation(CredentialRegistryMutationKind.SetHealth, Id($"matrix-terminal-{phase.ToString().ToLowerInvariant()}"), 2, ReferenceId(), null, null, null, health, null, LifecycleOperation: (int)CredentialLifecycleOperationKind.Create, ActorId: "user-1", LifecycleRequestHash: requestHash, LifecyclePhase: phase, LifecycleIntentOperationId: intentId, WorkspaceId: "workspace-1", LifecycleAudit: AuditPayload(auditOutcome));

        var result = await store.MutateAsync(terminal);
        var restarted = await Store(paths).ReadAsync();

        Assert.Equal(CredentialRegistryMutationStatus.Applied, result.Status);
        Assert.Equal(health, Assert.Single(restarted.Entries).Health);
        Assert.Equal(2, restarted.PendingAudits.Count);
        Assert.Equal(intent.OperationId, restarted.PendingAudits[0].AuditOperationId);
        Assert.Equal(AuditSchema.Actions.CredentialLifecycleIntent, restarted.PendingAudits[0].Action);
        Assert.Equal(terminal.OperationId, restarted.PendingAudits[1].AuditOperationId);
        Assert.Equal(auditOutcome, restarted.PendingAudits[1].Outcome);
        Assert.Equal(phase, Assert.Single(restarted.Operations, operation => operation.OperationId.Equals(terminal.OperationId)).LifecyclePhase);
    }

    [Fact]
    public async Task LocatorUncertaintyRemainsValueFreeAndCannotBeBypassedAcrossRestart()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = Store(paths);
        var intentId = Id("locator-uncertain-intent");
        var requestHash = Hash('9');
        var intent = new CredentialRegistryMutation(CredentialRegistryMutationKind.BeginCreate, intentId, 0, ReferenceId(), Reference(), Binding(), Id("locator-uncertain-consent"), CredentialProviderHealthStatus.NeedsRepair, null, false, (int)CredentialLifecycleOperationKind.Create, "user-1", null, requestHash, CredentialLifecycleMutationPhase.Intent, intentId, null, "workspace-1", IntentAuditPayload());
        Assert.Equal(CredentialRegistryMutationStatus.Applied, (await store.MutateAsync(intent)).Status);
        var uncertain = new CredentialRegistryMutation(CredentialRegistryMutationKind.RecordLocatorUncertain, Id("locator-uncertain-terminal"), 1, ReferenceId(), null, null, null, null, null, LifecycleOperation: (int)CredentialLifecycleOperationKind.Create, ActorId: "user-1", LifecycleRequestHash: requestHash, LifecyclePhase: CredentialLifecycleMutationPhase.LocatorUncertain, LifecycleIntentOperationId: intentId, WorkspaceId: "workspace-1", LifecycleAudit: AuditPayload(AuditSchema.Outcomes.Failed));
        Assert.Equal(CredentialRegistryMutationStatus.Applied, (await store.MutateAsync(uncertain)).Status);

        var restartedStore = Store(paths);
        var restarted = await restartedStore.ReadAsync();
        var bypass = await restartedStore.MutateAsync(Register(2));
        var competingId = Id("locator-uncertain-competing");
        var competing = intent with { OperationId = competingId, LifecycleIntentOperationId = competingId, ExpectedRegistryRevision = 2 };

        Assert.Empty(restarted.Entries);
        Assert.Empty(JsonNode.Parse(await File.ReadAllTextAsync(paths.CredentialRegistryPrivateDocumentPath))!["locators"]!.AsArray());
        Assert.Equal([CredentialLifecycleMutationPhase.Intent, CredentialLifecycleMutationPhase.LocatorUncertain], restarted.Operations.Select(item => item.LifecyclePhase).ToArray());
        Assert.Equal([AuditSchema.Actions.CredentialLifecycleIntent, AuditSchema.Actions.CredentialLifecycleOutcome], restarted.PendingAudits.Select(item => item.Action).ToArray());
        Assert.Equal(CredentialRegistryMutationStatus.Conflict, bypass.Status);
        Assert.Equal(CredentialRegistryMutationStatus.Conflict, (await restartedStore.MutateAsync(competing)).Status);
        Assert.Equal(2, (await restartedStore.ReadAsync()).RegistryRevision);
    }

    [Fact]
    public async Task PreparedCreateCanBeExplicitlyRepairedAcrossRestartWithoutLocatorLeakage()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = Store(paths);
        var createIntentId = Id("prepared-repair-create");
        var createHash = Hash('3');
        var createIntent = new CredentialRegistryMutation(CredentialRegistryMutationKind.BeginCreate, createIntentId, 0, ReferenceId(), Reference(), Binding(), Id("prepared-repair-consent"), CredentialProviderHealthStatus.NeedsRepair, null, false, (int)CredentialLifecycleOperationKind.Create, "user-1", null, createHash, CredentialLifecycleMutationPhase.Intent, createIntentId, null, "workspace-1", IntentAuditPayload());
        Assert.Equal(CredentialRegistryMutationStatus.Applied, (await store.MutateAsync(createIntent)).Status);
        var locatorPrepared = new CredentialRegistryMutation(CredentialRegistryMutationKind.Register, Id("prepared-repair-locator"), 1, ReferenceId(), Reference(), Binding(), Id("prepared-repair-consent"), CredentialProviderHealthStatus.NeedsRepair, Locator(), false, (int)CredentialLifecycleOperationKind.Create, "user-1", null, createHash, CredentialLifecycleMutationPhase.LocatorPrepared, createIntentId, null, "workspace-1");
        Assert.Equal(CredentialRegistryMutationStatus.Applied, (await store.MutateAsync(locatorPrepared)).Status);

        var restarted = Store(paths);
        Assert.Equal(CredentialRegistryMutationStatus.Replayed, (await restarted.MutateAsync(locatorPrepared)).Status);
        var directTombstone = new CredentialRegistryMutation(CredentialRegistryMutationKind.Tombstone, Id("prepared-repair-bypass"), 2, ReferenceId(), null, null, null, null, null);
        Assert.Equal(CredentialRegistryMutationStatus.Conflict, (await restarted.MutateAsync(directTombstone)).Status);
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
    public async Task LocatorUncertainCanFollowCommittedPreparedLocatorAtRefreshedRevision()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = Store(paths);
        var intentId = Id("prepared-ack-intent");
        var requestHash = Hash('7');
        var intent = new CredentialRegistryMutation(CredentialRegistryMutationKind.BeginCreate, intentId, 0, ReferenceId(), Reference(), Binding(), Id("prepared-ack-consent"), CredentialProviderHealthStatus.NeedsRepair, null, false, (int)CredentialLifecycleOperationKind.Create, "user-1", null, requestHash, CredentialLifecycleMutationPhase.Intent, intentId, null, "workspace-1", IntentAuditPayload());
        Assert.Equal(CredentialRegistryMutationStatus.Applied, (await store.MutateAsync(intent)).Status);
        var locatorPrepared = new CredentialRegistryMutation(CredentialRegistryMutationKind.Register, Id("prepared-ack-locator"), 1, ReferenceId(), Reference(), Binding(), Id("prepared-ack-consent"), CredentialProviderHealthStatus.NeedsRepair, Locator(), false, (int)CredentialLifecycleOperationKind.Create, "user-1", null, requestHash, CredentialLifecycleMutationPhase.LocatorPrepared, intentId, null, "workspace-1");
        Assert.Equal(CredentialRegistryMutationStatus.Applied, (await store.MutateAsync(locatorPrepared)).Status);
        var locatorUncertain = new CredentialRegistryMutation(CredentialRegistryMutationKind.RecordLocatorUncertain, Id("prepared-ack-uncertain"), 2, ReferenceId(), null, null, null, null, null, LifecycleOperation: (int)CredentialLifecycleOperationKind.Create, ActorId: "user-1", LifecycleRequestHash: requestHash, LifecyclePhase: CredentialLifecycleMutationPhase.LocatorUncertain, LifecycleIntentOperationId: intentId, WorkspaceId: "workspace-1", LifecycleAudit: AuditPayload(AuditSchema.Outcomes.Failed));
        Assert.Equal(CredentialRegistryMutationStatus.Applied, (await store.MutateAsync(locatorUncertain)).Status);

        var restarted = Store(paths);
        var read = await restarted.ReadAsync();
        var bypass = await restarted.MutateAsync(new CredentialRegistryMutation(CredentialRegistryMutationKind.Tombstone, Id("prepared-ack-bypass"), 3, ReferenceId(), null, null, null, null, null));

        Assert.Equal(CredentialRegistryMutationStatus.Conflict, bypass.Status);
        Assert.Equal(CredentialProviderHealthStatus.NeedsRepair, Assert.Single(read.Entries).Health);
        Assert.Equal([CredentialLifecycleMutationPhase.Intent, CredentialLifecycleMutationPhase.LocatorPrepared, CredentialLifecycleMutationPhase.LocatorUncertain], read.Operations.Select(item => item.LifecyclePhase).ToArray());
        Assert.Equal([AuditSchema.Actions.CredentialLifecycleIntent, AuditSchema.Actions.CredentialLifecycleOutcome], read.PendingAudits.Select(item => item.Action).ToArray());
        Assert.Single(JsonNode.Parse(await File.ReadAllTextAsync(paths.CredentialRegistryPrivateDocumentPath))!["locators"]!.AsArray());
        Assert.DoesNotContain(Locator().Value, await File.ReadAllTextAsync(paths.CredentialRegistryDocumentPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RawRepairAuthorityCannotBeForgedOrCompletedThroughPublicComposition()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = Store(paths);
        var createIntentId = Id("reconcile-prepared-create");
        var createHash = Hash('a');
        var createIntent = new CredentialRegistryMutation(CredentialRegistryMutationKind.BeginCreate, createIntentId, 0, ReferenceId(), Reference(), Binding(), Id("reconcile-prepared-consent"), CredentialProviderHealthStatus.NeedsRepair, null, false, (int)CredentialLifecycleOperationKind.Create, Environment.UserName, null, createHash, CredentialLifecycleMutationPhase.Intent, createIntentId, null, "workspace-1", IntentAuditPayload());
        Assert.Equal(CredentialRegistryMutationStatus.Applied, (await store.MutateAsync(createIntent)).Status);
        var locatorPrepared = new CredentialRegistryMutation(CredentialRegistryMutationKind.Register, Id("reconcile-prepared-locator"), 1, ReferenceId(), Reference(), Binding(), Id("reconcile-prepared-consent"), CredentialProviderHealthStatus.NeedsRepair, Locator(), false, (int)CredentialLifecycleOperationKind.Create, Environment.UserName, null, createHash, CredentialLifecycleMutationPhase.LocatorPrepared, createIntentId, null, "workspace-1");
        Assert.Equal(CredentialRegistryMutationStatus.Applied, (await store.MutateAsync(locatorPrepared)).Status);
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
        CredentialRegistryMutationKind[] publicRawKinds = [CredentialRegistryMutationKind.Register, CredentialRegistryMutationKind.SetHealth, CredentialRegistryMutationKind.Tombstone, CredentialRegistryMutationKind.Bind, CredentialRegistryMutationKind.Consent, CredentialRegistryMutationKind.UpdatePosture, CredentialRegistryMutationKind.BeginCreate, CredentialRegistryMutationKind.RecordLocatorUncertain];

        foreach (var kind in Enum.GetValues<CredentialRegistryMutationKind>())
        {
            var mutation = new CredentialRegistryMutation(kind, Id($"unclassified-{(int)kind}"), 0, ReferenceId(), null, null, null, null, null);
            var result = await store.MutateAsync(mutation);

            if (publicRawKinds.Contains(kind))
            {
                Assert.NotEqual(CredentialFailureCode.Unauthorized, result.Failure?.Code);
            }
            else
            {
                Assert.Equal(CredentialRegistryMutationStatus.Invalid, result.Status);
                Assert.Equal(CredentialFailureCode.Unauthorized, result.Failure!.Code);
            }
        }

    }

    [Fact]
    public async Task RawTombstoneRepairForgeryIsDeniedBeforeLegitimateRepair()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = Store(paths);
        Assert.Equal(CredentialRegistryMutationStatus.Applied, (await store.MutateAsync(Register(0))).Status);
        var deleteIntentId = Id("reconcile-tombstone-delete");
        var deleteHash = Hash('3');
        var deleteIntent = new CredentialRegistryMutation(CredentialRegistryMutationKind.UpdatePosture, deleteIntentId, 1, ReferenceId(), Reference(), null, null, CredentialProviderHealthStatus.NeedsRepair, null, LifecycleOperation: (int)CredentialLifecycleOperationKind.Delete, ActorId: "user-1", PreviewHash: Hash('4'), LifecycleRequestHash: deleteHash, LifecyclePhase: CredentialLifecycleMutationPhase.Intent, LifecycleIntentOperationId: deleteIntentId, WorkspaceId: "workspace-1", LifecycleAudit: IntentAuditPayload());
        Assert.Equal(CredentialRegistryMutationStatus.Applied, (await store.MutateAsync(deleteIntent)).Status);
        var tombstone = new CredentialRegistryMutation(CredentialRegistryMutationKind.Tombstone, Id("reconcile-tombstone-uncertain"), 2, ReferenceId(), null, null, null, null, null, LifecycleOperation: (int)CredentialLifecycleOperationKind.Delete, ActorId: "user-1", PreviewHash: Hash('4'), LifecycleRequestHash: deleteHash, LifecyclePhase: CredentialLifecycleMutationPhase.TombstoneUncertain, LifecycleIntentOperationId: deleteIntentId, WorkspaceId: "workspace-1", LifecycleAudit: AuditPayload(AuditSchema.Outcomes.Failed));
        Assert.Equal(CredentialRegistryMutationStatus.Applied, (await store.MutateAsync(tombstone)).Status);
        var originalTombstone = JsonNode.Parse(await File.ReadAllTextAsync(paths.CredentialRegistryDocumentPath))!["tombstones"]![0]!.ToJsonString();
        var interruptedRepairId = Id("reconcile-tombstone-interrupted");
        var interruptedRepair = new CredentialRegistryMutation(CredentialRegistryMutationKind.BeginRepair, interruptedRepairId, 3, ReferenceId(), null, null, null, null, null, LifecycleOperation: (int)CredentialLifecycleOperationKind.Repair, ActorId: Environment.UserName, PreviewHash: Hash('5'), LifecycleRequestHash: Hash('6'), LifecyclePhase: CredentialLifecycleMutationPhase.Intent, LifecycleIntentOperationId: interruptedRepairId, WorkspaceId: "workspace-1", LifecycleAudit: IntentAuditPayload());
        var forgedIntent = await store.MutateAsync(interruptedRepair);
        Assert.Equal(CredentialRegistryMutationStatus.Invalid, forgedIntent.Status);
        Assert.Equal(CredentialFailureCode.Unauthorized, forgedIntent.Failure!.Code);

        var service = ReconciliationService(paths);
        var reconcilePreview = await service.PreviewAsync(new CredentialLifecyclePreviewRequest(Id("reconcile-tombstone-terminal"), CredentialLifecycleOperationKind.ReconcileRepair, ReferenceId(), "workspace-1", Environment.UserName, 3, interruptedRepairId));
        Assert.Equal(CredentialLifecyclePreviewStatus.Conflict, reconcilePreview.Status);
        var repairPreview = await service.PreviewAsync(new CredentialLifecyclePreviewRequest(Id("reconcile-tombstone-repair"), CredentialLifecycleOperationKind.Repair, ReferenceId(), "workspace-1", Environment.UserName, 3));
        Assert.Equal(CredentialLifecyclePreviewStatus.Ready, repairPreview.Status);
        var repair = new CredentialLifecycleRequest(CredentialLifecycleOperationKind.Repair, Id("reconcile-tombstone-repair"), ReferenceId(), "workspace-1", Environment.UserName, 3, new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero), Preview: repairPreview, Confirmed: true);
        Assert.Equal(CredentialLifecycleResultStatus.Applied, (await service.ExecuteAsync(repair)).Status);

        var restarted = await Store(paths).ReadAsync();
        Assert.Empty(restarted.Entries);
        Assert.False(Assert.Single(restarted.Tombstones).NeedsRepair);
        Assert.Equal(originalTombstone, JsonNode.Parse(await File.ReadAllTextAsync(paths.CredentialRegistryDocumentPath))!["tombstones"]![0]!.ToJsonString());
        Assert.Empty(JsonNode.Parse(await File.ReadAllTextAsync(paths.CredentialRegistryPrivateDocumentPath))!["locators"]!.AsArray());
        Assert.Equal([CredentialLifecycleMutationPhase.Intent, CredentialLifecycleMutationPhase.TombstoneUncertain, CredentialLifecycleMutationPhase.Intent, CredentialLifecycleMutationPhase.RepairComplete], restarted.Operations.Where(operation => operation.LifecyclePhase is not null).Select(operation => operation.LifecyclePhase).ToArray());
        Assert.Empty(restarted.PendingAudits);
    }

    [Fact]
    public async Task UncertainTombstoneRetainsPrivateLocatorAcrossRestartUntilExplicitRepairCompletion()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = Store(paths);
        Assert.Equal(CredentialRegistryMutationStatus.Applied, (await store.MutateAsync(Register(0))).Status);
        var deleteIntentId = Id("repair-delete-intent");
        var previewHash = Hash('e');
        var requestHash = Hash('f');
        var intent = new CredentialRegistryMutation(CredentialRegistryMutationKind.UpdatePosture, deleteIntentId, 1, ReferenceId(), Reference(), null, null, CredentialProviderHealthStatus.NeedsRepair, null, LifecycleOperation: (int)CredentialLifecycleOperationKind.Delete, ActorId: "user-1", PreviewHash: previewHash, LifecycleRequestHash: requestHash, LifecyclePhase: CredentialLifecycleMutationPhase.Intent, LifecycleIntentOperationId: deleteIntentId, WorkspaceId: "workspace-1", LifecycleAudit: IntentAuditPayload());
        Assert.Equal(CredentialRegistryMutationStatus.Applied, (await store.MutateAsync(intent)).Status);
        var tombstone = new CredentialRegistryMutation(CredentialRegistryMutationKind.Tombstone, Id("repair-delete-tombstone"), 2, ReferenceId(), null, null, null, null, null, LifecycleOperation: (int)CredentialLifecycleOperationKind.Delete, ActorId: "user-1", PreviewHash: previewHash, LifecycleRequestHash: requestHash, LifecyclePhase: CredentialLifecycleMutationPhase.TombstoneUncertain, LifecycleIntentOperationId: deleteIntentId, WorkspaceId: "workspace-1", LifecycleAudit: AuditPayload("failed"));
        Assert.Equal(CredentialRegistryMutationStatus.Applied, (await store.MutateAsync(tombstone)).Status);
        var originalTombstone = JsonNode.Parse(await File.ReadAllTextAsync(paths.CredentialRegistryDocumentPath))!["tombstones"]![0]!.ToJsonString();

        var restarted = await Store(paths).ReadAsync();
        var repairRequired = Assert.Single(restarted.Tombstones);
        Assert.True(repairRequired.NeedsRepair);
        Assert.NotNull(repairRequired.RepairBinding);
        var retainedLocator = Assert.Single(JsonNode.Parse(await File.ReadAllTextAsync(paths.CredentialRegistryPrivateDocumentPath))!["locators"]!.AsArray());
        Assert.Equal(Locator().Value, retainedLocator!["locator"]!.GetValue<string>());

        var uncertainService = ReconciliationService(paths, deleteSucceeds: false);
        var uncertainPreview = await uncertainService.PreviewAsync(new CredentialLifecyclePreviewRequest(Id("repair-uncertain-intent"), CredentialLifecycleOperationKind.Repair, ReferenceId(), "workspace-1", Environment.UserName, 3));
        Assert.Equal(CredentialLifecyclePreviewStatus.Ready, uncertainPreview.Status);
        var uncertainRepair = new CredentialLifecycleRequest(CredentialLifecycleOperationKind.Repair, Id("repair-uncertain-intent"), ReferenceId(), "workspace-1", Environment.UserName, 3, new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero), Preview: uncertainPreview, Confirmed: true);
        Assert.Equal(CredentialLifecycleResultStatus.NeedsRepair, (await uncertainService.ExecuteAsync(uncertainRepair)).Status);
        Assert.True(Assert.Single((await Store(paths).ReadAsync()).Tombstones).NeedsRepair);
        var finalService = ReconciliationService(paths);
        var finalPreview = await finalService.PreviewAsync(new CredentialLifecyclePreviewRequest(Id("repair-explicit-intent"), CredentialLifecycleOperationKind.Repair, ReferenceId(), "workspace-1", Environment.UserName, 5));
        Assert.Equal(CredentialLifecyclePreviewStatus.Ready, finalPreview.Status);
        var finalRepair = new CredentialLifecycleRequest(CredentialLifecycleOperationKind.Repair, Id("repair-explicit-intent"), ReferenceId(), "workspace-1", Environment.UserName, 5, new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero), Preview: finalPreview, Confirmed: true);
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
        var store = Store(new WorkspacePaths(workspace.RootPath));
        Assert.Equal(CredentialRegistryMutationStatus.Applied, (await store.MutateAsync(Register(0))).Status);
        var disabledReference = Reference() with { Status = CredentialLifecycleStatus.Disabled, UpdatedAtUtc = new DateTimeOffset(2026, 8, 1, 12, 1, 0, TimeSpan.Zero) };
        var disable = new CredentialRegistryMutation(CredentialRegistryMutationKind.UpdatePosture, Id("disable-safe"), 1, ReferenceId(), disabledReference, null, null, CredentialProviderHealthStatus.Disabled, null, LifecycleOperation: (int)CredentialLifecycleOperationKind.Disable, ActorId: "user-1", PreviewHash: Hash('3'), AffectedActiveRuns: []);
        Assert.Equal(CredentialRegistryMutationStatus.Applied, (await store.MutateAsync(disable)).Status);

        var widened = await store.MutateAsync(new CredentialRegistryMutation(CredentialRegistryMutationKind.SetHealth, Id("widen-health"), 2, ReferenceId(), null, null, null, CredentialProviderHealthStatus.Available, null));

        Assert.Equal(CredentialRegistryMutationStatus.Conflict, widened.Status);
        var entry = Assert.Single((await store.ReadAsync()).Entries);
        Assert.Equal(CredentialLifecycleStatus.Disabled, entry.Reference.Status);
        Assert.Equal(CredentialProviderHealthStatus.Disabled, entry.Health);
    }

    [Fact]
    public async Task UnresolvedProviderIntentCannotBeWidenedOrUsedByDirectStoreCalls()
    {
        using var workspace = new TestWorkspace();
        var store = Store(new WorkspacePaths(workspace.RootPath));
        Assert.Equal(CredentialRegistryMutationStatus.Applied, (await store.MutateAsync(Register(0))).Status);
        var intentId = Id("store-unresolved-intent");
        var intent = new CredentialRegistryMutation(CredentialRegistryMutationKind.UpdatePosture, intentId, 1, ReferenceId(), Reference(), null, null, CredentialProviderHealthStatus.NeedsRepair, null, LifecycleOperation: (int)CredentialLifecycleOperationKind.Replace, ActorId: "user-1", PreviewHash: Hash('4'), LifecycleRequestHash: Hash('5'), LifecyclePhase: CredentialLifecycleMutationPhase.Intent, LifecycleIntentOperationId: intentId, WorkspaceId: "workspace-1", LifecycleAudit: IntentAuditPayload());
        Assert.Equal(CredentialRegistryMutationStatus.Applied, (await store.MutateAsync(intent)).Status);

        var widened = await store.MutateAsync(new CredentialRegistryMutation(CredentialRegistryMutationKind.SetHealth, Id("store-unresolved-widen"), 2, ReferenceId(), null, null, null, CredentialProviderHealthStatus.Available, null));
        var consent = await store.MutateAsync(new CredentialRegistryMutation(CredentialRegistryMutationKind.Consent, Id("store-unresolved-consent"), 2, ReferenceId(), null, null, Id("store-unresolved-consent-document"), null, null, true));
        var evidence = await store.AppendAsync(Evidence(Binding(), "store-unresolved-evidence"), default);

        Assert.Equal(CredentialRegistryMutationStatus.Conflict, widened.Status);
        Assert.Equal(CredentialRegistryMutationStatus.Conflict, consent.Status);
        Assert.False(evidence.Succeeded);
        Assert.Equal(CredentialFailureCode.Conflict, evidence.Failure!.Code);
        Assert.Equal(2, (await store.ReadAsync()).RegistryRevision);
        Assert.Equal(CredentialProviderHealthStatus.NeedsRepair, Assert.Single((await store.ReadAsync()).Entries).Health);
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
        var store = Store(paths, new FixedTimeProvider());
        var registered = await store.MutateAsync(Register(0));
        Assert.Equal(CredentialRegistryMutationStatus.Applied, registered.Status);
        Assert.Equal(1, registered.RegistryRevision);

        var binding = Binding();
        var evidence = Evidence(binding);
        Assert.True((await store.AppendAsync(evidence, default)).Succeeded);
        var tombstone = await store.MutateAsync(new CredentialRegistryMutation(CredentialRegistryMutationKind.Tombstone, Id("tombstone-1"), 2, ReferenceId(), null, null, null, null, null));
        Assert.Equal(CredentialRegistryMutationStatus.Applied, tombstone.Status);

        var restarted = await Store(paths).ReadAsync();
        Assert.True(restarted.Succeeded);
        Assert.Equal(3, restarted.RegistryRevision);
        Assert.Empty(restarted.Entries);
        var savedTombstone = Assert.Single(restarted.Tombstones);
        Assert.Equal("credential-1", savedTombstone.ReferenceId.Value);
        Assert.Equal("tombstone-1", savedTombstone.OperationId.Value);
        Assert.Equal(["register-1", "evidence-1", "tombstone-1"], restarted.Operations.Select(item => item.OperationId.Value));
        Assert.Equal("evidence-1", Assert.Single(restarted.Evidence).EvidenceId.Value);
    }

    [Fact]
    public async Task Retry_and_stale_or_changed_operation_fail_closed()
    {
        using var workspace = new TestWorkspace();
        var store = Store(new WorkspacePaths(workspace.RootPath));
        var first = await store.MutateAsync(Register(0));
        var replay = await store.MutateAsync(Register(0));
        var stale = await store.MutateAsync(new CredentialRegistryMutation(CredentialRegistryMutationKind.SetHealth, Id("health-1"), 0, ReferenceId(), null, null, null, CredentialProviderHealthStatus.Available, null));
        var changed = await store.MutateAsync(new CredentialRegistryMutation(CredentialRegistryMutationKind.SetHealth, Id("register-1"), 1, ReferenceId(), null, null, null, CredentialProviderHealthStatus.Available, null));

        Assert.Equal(CredentialRegistryMutationStatus.Applied, first.Status);
        Assert.Equal(CredentialRegistryMutationStatus.Replayed, replay.Status);
        Assert.Equal(CredentialRegistryMutationStatus.Conflict, stale.Status);
        Assert.Equal(CredentialRegistryMutationStatus.Conflict, changed.Status);
    }

    [Fact]
    public async Task Partial_primary_recovers_only_from_last_proved_pair_and_workspace_substitution_fails()
    {
        using var source = new TestWorkspace();
        var sourcePaths = new WorkspacePaths(source.RootPath);
        var sourceStore = Store(sourcePaths);
        Assert.Equal(CredentialRegistryMutationStatus.Applied, (await sourceStore.MutateAsync(Register(0))).Status);
        Assert.Equal(CredentialRegistryMutationStatus.Applied, (await sourceStore.MutateAsync(new CredentialRegistryMutation(CredentialRegistryMutationKind.SetHealth, Id("health-1"), 1, ReferenceId(), null, null, null, CredentialProviderHealthStatus.Corrupt, null))).Status);

        await File.WriteAllTextAsync(sourcePaths.CredentialRegistryPrivateDocumentPath, "{");
        var recovered = await Store(sourcePaths).ReadAsync();
        Assert.True(recovered.Succeeded);
        Assert.Equal(1, recovered.RegistryRevision);
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
        Assert.Equal(CredentialRegistryMutationStatus.Applied, (await store.MutateAsync(Register(0))).Status);
        var publicText = await File.ReadAllTextAsync(paths.CredentialRegistryDocumentPath);
        var privateText = await File.ReadAllTextAsync(paths.CredentialRegistryPrivateDocumentPath);
        Assert.DoesNotContain(Locator().Value, publicText, StringComparison.Ordinal);
        Assert.Contains(Locator().Value, privateText, StringComparison.Ordinal);
        Assert.DoesNotContain("plaintext-secret-canary", publicText, StringComparison.Ordinal);
        Assert.DoesNotContain("ciphertext-envelope-canary", publicText, StringComparison.Ordinal);
        Assert.DoesNotContain("key-material-canary", publicText, StringComparison.Ordinal);

        var unsafeLocator = new CredentialRegistryMutation(CredentialRegistryMutationKind.Register, Id("unsafe-1"), 1, ReferenceId(), Reference(), Binding(), Id("consent-1"), CredentialProviderHealthStatus.Available, null);
        Assert.Equal(CredentialRegistryMutationStatus.Invalid, (await store.MutateAsync(unsafeLocator)).Status);
    }

    [Fact]
    public async Task Concurrent_optimistic_mutations_admit_exactly_one_writer()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Store(paths).MutateAsync(Register(0))));
        Assert.Equal(1, attempts.Count(item => item.Status == CredentialRegistryMutationStatus.Applied));
        Assert.Equal(7, attempts.Count(item => item.Status is CredentialRegistryMutationStatus.Conflict or CredentialRegistryMutationStatus.Replayed));
        Assert.Equal(1, (await Store(paths).ReadAsync()).RegistryRevision);
    }

    [Fact]
    public async Task Unsupported_or_fully_corrupt_artifacts_fail_closed_without_plaintext_fallback()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = Store(paths);
        Assert.Equal(CredentialRegistryMutationStatus.Applied, (await store.MutateAsync(Register(0))).Status);
        await File.WriteAllTextAsync(paths.CredentialRegistryDocumentPath, "{\"schemaVersion\":2}");
        await File.WriteAllTextAsync(paths.CredentialRegistryPrivateDocumentPath, "{\"schemaVersion\":2}");
        await File.WriteAllTextAsync(paths.CredentialRegistryProofPath, "plaintext-secret-canary");
        await File.WriteAllTextAsync(paths.CredentialRegistryPrivateProofPath, "ciphertext-envelope-canary");

        var read = await Store(paths).ReadAsync();
        Assert.False(read.Succeeded);
        Assert.Equal(CredentialFailureCode.Unavailable, read.Failure!.Code);
        var mutation = await Store(paths).MutateAsync(new CredentialRegistryMutation(CredentialRegistryMutationKind.SetHealth, Id("health-1"), 1, ReferenceId(), null, null, null, CredentialProviderHealthStatus.Available, null));
        Assert.Equal(CredentialRegistryMutationStatus.Unavailable, mutation.Status);
        var corruptStore = Store(paths);
        Assert.Equal(CredentialFailureCode.Unavailable, (await corruptStore.GetAsync(ReferenceId(), default)).Failure!.Code);
        Assert.False(await corruptStore.AcknowledgeAuditAsync(Id("corrupt-audit")));
        Assert.Equal(CredentialFailureCode.Unavailable, (await corruptStore.AppendAsync(Evidence(Binding()), default)).Failure!.Code);
    }

    [Fact]
    public async Task Audit_delivery_acknowledgement_is_durable_and_idempotent()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = Store(paths);
        var intentId = Id("ack-intent");
        var intent = new CredentialRegistryMutation(CredentialRegistryMutationKind.BeginCreate, intentId, 0, ReferenceId(), Reference(), Binding(), Id("ack-consent"), CredentialProviderHealthStatus.NeedsRepair, null, false, (int)CredentialLifecycleOperationKind.Create, "user-1", null, Hash('a'), CredentialLifecycleMutationPhase.Intent, intentId, null, "workspace-1", IntentAuditPayload());

        Assert.Equal(CredentialRegistryMutationStatus.Applied, (await store.MutateAsync(intent)).Status);
        Assert.True(await store.AcknowledgeAuditAsync(intentId));
        Assert.True(await Store(paths).AcknowledgeAuditAsync(intentId));
        Assert.Empty((await Store(paths).ReadAsync()).PendingAudits);
    }

    [Fact]
    public async Task AuthenticatedPriorSchemaOneShapeIsRejectedWithoutRewriteOrMigration()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var bootstrapTrust = new TestCapabilityLifecycleTrustProvider();
        var bootstrap = new CredentialRegistryStore(paths, bootstrapTrust, new AcceptingLocatorVerifier());
        Assert.Equal(CredentialRegistryMutationStatus.Applied, (await bootstrap.MutateAsync(Register(0))).Status);
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
        var bootstrapTrust = new TestCapabilityLifecycleTrustProvider();
        var bootstrap = new CredentialRegistryStore(paths, bootstrapTrust, new AcceptingLocatorVerifier());
        Assert.Equal(CredentialRegistryMutationStatus.Applied, (await bootstrap.MutateAsync(Register(0))).Status);
        var intentId = Id("prior-outbox-intent");
        var intent = new CredentialRegistryMutation(CredentialRegistryMutationKind.UpdatePosture, intentId, 1, ReferenceId(), Reference(), null, null, CredentialProviderHealthStatus.NeedsRepair, null, LifecycleOperation: (int)CredentialLifecycleOperationKind.Replace, ActorId: "user-1", LifecycleRequestHash: Hash('8'), LifecyclePhase: CredentialLifecycleMutationPhase.Intent, LifecycleIntentOperationId: intentId, WorkspaceId: "workspace-1", LifecycleAudit: IntentAuditPayload());
        Assert.Equal(CredentialRegistryMutationStatus.Applied, (await bootstrap.MutateAsync(intent)).Status);
        var publicNode = JsonNode.Parse(await File.ReadAllTextAsync(paths.CredentialRegistryDocumentPath))!.AsObject();
        var privateNode = JsonNode.Parse(await File.ReadAllTextAsync(paths.CredentialRegistryPrivateDocumentPath))!.AsObject();
        var outbox = publicNode["operations"]!.AsArray()[1]!["auditOutbox"]!.AsObject();
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
        var bootstrapTrust = new TestCapabilityLifecycleTrustProvider();
        var bootstrap = new CredentialRegistryStore(paths, bootstrapTrust, new AcceptingLocatorVerifier());
        Assert.Equal(CredentialRegistryMutationStatus.Applied, (await bootstrap.MutateAsync(Register(1, 0))).Status);
        Assert.Equal(CredentialRegistryMutationStatus.Applied, (await bootstrap.MutateAsync(Register(2, 1))).Status);
        if (corruption == "invalid-tombstone")
        {
            var tombstone = new CredentialRegistryMutation(CredentialRegistryMutationKind.Tombstone, Id("corrupt-tombstone"), 2, ReferenceId(1), null, null, null, null, null);
            Assert.Equal(CredentialRegistryMutationStatus.Applied, (await bootstrap.MutateAsync(tombstone)).Status);
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
        var store = Store(new WorkspacePaths(workspace.RootPath));
        var binding = Binding();
        Assert.False((await store.AppendAsync(Evidence(binding), default)).Succeeded);
        Assert.Equal(CredentialRegistryMutationStatus.Applied, (await store.MutateAsync(Register(0))).Status);
        Assert.True((await store.AppendAsync(Evidence(binding), default)).Succeeded);
    }

    [Fact]
    public async Task Evidence_scope_must_be_equal_to_or_narrower_than_the_registered_binding()
    {
        using var workspace = new TestWorkspace();
        var store = Store(new WorkspacePaths(workspace.RootPath));
        var binding = Binding();
        Assert.Equal(CredentialRegistryMutationStatus.Applied, (await store.MutateAsync(Register(0))).Status);

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
        Assert.Equal(2, current.RegistryRevision);
        Assert.Equal("narrower-1", Assert.Single(current.Evidence).EvidenceId.Value);
    }

    [Fact]
    public async Task Shape_correct_locator_is_rejected_without_explicit_provider_ownership_verification()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var result = await new CredentialRegistryStore(paths, TestTrust(paths), new RejectingCredentialProviderLocatorVerifier()).MutateAsync(Register(0));
        Assert.Equal(CredentialRegistryMutationStatus.Unavailable, result.Status);
        Assert.False(File.Exists(paths.CredentialRegistryDocumentPath));
        Assert.False(File.Exists(paths.CredentialRegistryPrivateDocumentPath));
    }

    [Fact]
    public async Task Candidate_durability_failure_recovers_only_the_previously_proved_snapshot()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var barrier = new FailOnDurabilityCallBarrier(8);
        var store = new CredentialRegistryStore(paths, TestTrust(paths), new AcceptingLocatorVerifier(), durabilityBarrier: barrier);
        Assert.Equal(CredentialRegistryMutationStatus.Applied, (await store.MutateAsync(Register(0))).Status);

        var failed = await store.MutateAsync(new CredentialRegistryMutation(CredentialRegistryMutationKind.SetHealth, Id("health-1"), 1, ReferenceId(), null, null, null, CredentialProviderHealthStatus.Corrupt, null));
        Assert.Equal(CredentialRegistryMutationStatus.Unavailable, failed.Status);

        var recovered = await Store(paths).ReadAsync();
        Assert.True(recovered.Succeeded);
        Assert.Equal(1, recovered.RegistryRevision);
        Assert.Equal(CredentialProviderHealthStatus.Available, Assert.Single(recovered.Entries).Health);
        Assert.Equal(CredentialRegistryMutationStatus.Unavailable, (await Store(paths).MutateAsync(new CredentialRegistryMutation(CredentialRegistryMutationKind.SetHealth, Id("health-2"), 1, ReferenceId(), null, null, null, CredentialProviderHealthStatus.Corrupt, null))).Status);
    }

    [Fact]
    public async Task Trust_anchor_advance_failure_never_acknowledges_an_untrusted_successor()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new FailingCapabilityCatalogTrustProvider(TestTrust(paths));
        var store = new CredentialRegistryStore(paths, trust, new AcceptingLocatorVerifier());
        Assert.Equal(CredentialRegistryMutationStatus.Applied, (await store.MutateAsync(Register(0))).Status);
        trust.FailNextAdvance = true;

        var failed = await store.MutateAsync(new CredentialRegistryMutation(CredentialRegistryMutationKind.SetHealth, Id("health-1"), 1, ReferenceId(), null, null, null, CredentialProviderHealthStatus.Corrupt, null));
        Assert.Equal(CredentialRegistryMutationStatus.Unavailable, failed.Status);
        var recovered = await Store(paths).ReadAsync();
        Assert.True(recovered.Succeeded);
        Assert.Equal(1, recovered.RegistryRevision);
        Assert.Equal(CredentialProviderHealthStatus.Available, Assert.Single(recovered.Entries).Health);
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
        var provider = new FileCapabilityCatalogTrustProvider(trustRoot.RootPath);
        var interrupted = new CredentialRegistryStore(paths, provider, new AcceptingLocatorVerifier(), durabilityBarrier: new FailOnDurabilityCallBarrier(failingWrite));

        var failed = await interrupted.MutateAsync(Register(0));

        Assert.Equal(CredentialRegistryMutationStatus.Unavailable, failed.Status);
        var restarted = new CredentialRegistryStore(paths, provider, new AcceptingLocatorVerifier());
        var empty = await restarted.ReadAsync();
        Assert.True(empty.Succeeded);
        Assert.Equal(0, empty.RegistryRevision);
        Assert.Empty(empty.Entries);

        var retried = await restarted.MutateAsync(Register(0));

        Assert.Equal(CredentialRegistryMutationStatus.Applied, retried.Status);
        var completed = await new CredentialRegistryStore(paths, provider, new AcceptingLocatorVerifier()).ReadAsync();
        Assert.True(completed.Succeeded);
        Assert.Equal(1, completed.RegistryRevision);
        Assert.Single(completed.Entries);
    }

    [Fact]
    public async Task Rehashed_state_digests_cannot_reuse_an_authenticated_public_content_digest_and_tag()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        Assert.Equal(CredentialRegistryMutationStatus.Applied, (await Store(paths).MutateAsync(Register(0))).Status);
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
        var store = Store(paths);
        Assert.Equal(CredentialRegistryMutationStatus.Applied, (await store.MutateAsync(Register(0))).Status);

        using var externalLock = new FileStream(paths.CredentialRegistryLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var blocked = await Store(paths).MutateAsync(new CredentialRegistryMutation(CredentialRegistryMutationKind.SetHealth, Id("health-1"), 1, ReferenceId(), null, null, null, CredentialProviderHealthStatus.Corrupt, null), cancellation.Token);

        Assert.Equal(CredentialRegistryMutationStatus.Unavailable, blocked.Status);
        externalLock.Dispose();
        var current = await store.ReadAsync();
        Assert.True(current.Succeeded);
        Assert.Equal(1, current.RegistryRevision);
        Assert.Equal(CredentialProviderHealthStatus.Available, Assert.Single(current.Entries).Health);
    }

    [Fact]
    public async Task Same_physical_workspace_rollback_is_rejected_by_the_monotonic_trust_anchor()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = Store(paths);
        Assert.Equal(CredentialRegistryMutationStatus.Applied, (await store.MutateAsync(Register(0))).Status);
        var oldPublic = await File.ReadAllBytesAsync(paths.CredentialRegistryDocumentPath);
        var oldPrivate = await File.ReadAllBytesAsync(paths.CredentialRegistryPrivateDocumentPath);
        Assert.Equal(CredentialRegistryMutationStatus.Applied, (await store.MutateAsync(new CredentialRegistryMutation(CredentialRegistryMutationKind.SetHealth, Id("health-1"), 1, ReferenceId(), null, null, null, CredentialProviderHealthStatus.Corrupt, null))).Status);
        Assert.True((await store.AppendAsync(Evidence(Binding()), default)).Succeeded);

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
        var store = Store(new WorkspacePaths(workspace.RootPath));
        var original = await store.MutateAsync(Register(0));
        Assert.Equal(CredentialRegistryMutationStatus.Applied, original.Status);
        Assert.Equal(CredentialRegistryMutationStatus.Applied, (await store.MutateAsync(new CredentialRegistryMutation(CredentialRegistryMutationKind.SetHealth, Id("health-1"), 1, ReferenceId(), null, null, null, CredentialProviderHealthStatus.Corrupt, null))).Status);
        Assert.Equal(CredentialRegistryMutationStatus.Applied, (await store.MutateAsync(new CredentialRegistryMutation(CredentialRegistryMutationKind.Tombstone, Id("tombstone-1"), 2, ReferenceId(), null, null, null, null, null))).Status);

        var replay = await store.MutateAsync(Register(0));
        Assert.Equal(CredentialRegistryMutationStatus.Replayed, replay.Status);
        Assert.Equal(1, replay.RegistryRevision);
        Assert.NotNull(replay.Entry);
        Assert.Equal(CredentialProviderHealthStatus.Available, replay.Entry!.Health);
        Assert.Equal(1, replay.Entry.Revision);
    }

    [Fact]
    public async Task Cancellation_while_trust_is_unavailable_does_not_acknowledge_or_poison_a_mutation()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new BlockingCapabilityCatalogTrustProvider(TestTrust(paths));
        var store = new CredentialRegistryStore(paths, trust, new AcceptingLocatorVerifier());
        Assert.Equal(CredentialRegistryMutationStatus.Applied, (await store.MutateAsync(Register(0))).Status);

        trust.BlockNextRead = true;
        using var cancellation = new CancellationTokenSource();
        var pending = store.MutateAsync(new CredentialRegistryMutation(CredentialRegistryMutationKind.SetHealth, Id("health-1"), 1, ReferenceId(), null, null, null, CredentialProviderHealthStatus.Corrupt, null), cancellation.Token);
        await trust.Entered;
        cancellation.Cancel();
        var cancelled = await pending;
        trust.Release();

        Assert.Equal(CredentialRegistryMutationStatus.Unavailable, cancelled.Status);
        var current = await Store(paths).ReadAsync();
        Assert.True(current.Succeeded);
        Assert.Equal(1, current.RegistryRevision);
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
        var store = Store(paths);
        Assert.Equal(CredentialRegistryMutationStatus.Applied, (await store.MutateAsync(Register(0))).Status);

        Directory.Delete(paths.CredentialRegistryPath, recursive: true);
        Directory.CreateSymbolicLink(paths.CredentialRegistryPath, outside.RootPath);

        var read = await Store(paths).ReadAsync();
        var mutation = await Store(paths).MutateAsync(new CredentialRegistryMutation(CredentialRegistryMutationKind.SetHealth, Id("health-1"), 1, ReferenceId(), null, null, null, CredentialProviderHealthStatus.Corrupt, null));
        Assert.False(read.Succeeded);
        Assert.Equal(CredentialFailureCode.Unavailable, read.Failure!.Code);
        Assert.Equal(CredentialRegistryMutationStatus.Unavailable, mutation.Status);
        Assert.Empty(Directory.EnumerateFiles(outside.RootPath, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Submitted_locator_canary_crosses_only_the_verifier_and_private_artifact_boundary()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var verifier = new RecordingLocatorVerifier();
        var store = new CredentialRegistryStore(paths, TestTrust(paths), verifier);
        var locatorCanary = Locator("loc_c0dec0dec0dec0dec0dec0dec0dec0dec0dec0dec0dec0dec0dec0dec0dec0de");

        var registered = await store.MutateAsync(Register(1, 0, locatorCanary));
        Assert.Equal(CredentialRegistryMutationStatus.Applied, registered.Status);
        Assert.Equal(locatorCanary.Value, Assert.Single(verifier.Locators));
        Assert.Equal(CredentialRegistryMutationStatus.Applied, (await store.MutateAsync(new CredentialRegistryMutation(CredentialRegistryMutationKind.SetHealth, Id("health-canary"), 1, ReferenceId(), null, null, null, CredentialProviderHealthStatus.Corrupt, null))).Status);

        var publicArtifacts = new[] { await File.ReadAllTextAsync(paths.CredentialRegistryDocumentPath), await File.ReadAllTextAsync(paths.CredentialRegistryProofPath) };
        var privateArtifacts = new[] { await File.ReadAllTextAsync(paths.CredentialRegistryPrivateDocumentPath), await File.ReadAllTextAsync(paths.CredentialRegistryPrivateProofPath) };
        Assert.All(publicArtifacts, artifact => Assert.DoesNotContain(locatorCanary.Value, artifact, StringComparison.Ordinal));
        Assert.All(privateArtifacts, artifact => Assert.Contains(locatorCanary.Value, artifact, StringComparison.Ordinal));
        Assert.DoesNotContain(locatorCanary.Value, JsonSerializer.Serialize(registered), StringComparison.Ordinal);
        Assert.DoesNotContain(locatorCanary.Value, JsonSerializer.Serialize(await store.ReadAsync()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Entry_quota_is_preflighted_without_recording_the_rejected_operation()
    {
        using var workspace = new TestWorkspace();
        var quota = new CredentialRegistryQuota(2, 2, 4, 4, 128 * 1024);
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new CredentialRegistryStore(paths, TestTrust(paths), new AcceptingLocatorVerifier(), quota: quota);
        for (var index = 0; index < quota.MaximumEntries; index++)
        {
            Assert.Equal(CredentialRegistryMutationStatus.Applied, (await store.MutateAsync(Register(index, index))).Status);
        }

        var rejected = await store.MutateAsync(Register(quota.MaximumEntries, quota.MaximumEntries));
        Assert.Equal(CredentialRegistryMutationStatus.Unavailable, rejected.Status);
        Assert.Equal(CredentialFailureCode.LimitExceeded, rejected.Failure!.Code);
        var current = await store.ReadAsync();
        Assert.True(current.Succeeded);
        Assert.Equal(quota.MaximumEntries, current.Entries.Count);
        Assert.Equal(quota.MaximumEntries, current.Operations.Count);
        Assert.DoesNotContain(current.Operations, operation => operation.OperationId.Value == $"register-{quota.MaximumEntries}");
    }

    [Fact]
    public async Task Artifact_byte_quota_is_preflighted_before_any_registry_artifact_is_written()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new LongAuthenticationTagTrustProvider(TestTrust(paths), 2048);
        var quota = new CredentialRegistryQuota(2, 2, 4, 4, 4096);
        var store = new CredentialRegistryStore(paths, trust, new AcceptingLocatorVerifier(), quota: quota);

        var rejected = await store.MutateAsync(Register(0));
        Assert.Equal(CredentialRegistryMutationStatus.Unavailable, rejected.Status);
        Assert.Equal(0, trust.InitializeCount);
        Assert.Equal(0, trust.AuthenticateCount);
        Assert.False(File.Exists(paths.CredentialRegistryDocumentPath));
        Assert.False(File.Exists(paths.CredentialRegistryPrivateDocumentPath));
        Assert.False(File.Exists(paths.CredentialRegistryProofPath));
        Assert.False(File.Exists(paths.CredentialRegistryPrivateProofPath));

        var valid = await Store(paths).MutateAsync(Register(0));
        Assert.Equal(CredentialRegistryMutationStatus.Applied, valid.Status);
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
        Assert.Equal(CredentialRegistryMutationStatus.Unavailable, (await store.MutateAsync(Register(0), cancelled.Token)).Status);
        Assert.False(await store.AcknowledgeAuditAsync(Id("cancelled-audit"), cancelled.Token));
        Assert.Equal(CredentialFailureCode.Unavailable, (await store.AppendAsync(Evidence(Binding()), cancelled.Token)).Failure!.Code);

        Directory.CreateDirectory(paths.CredentialRegistryLockPath);
        var unsafeStore = Store(paths);
        Assert.False(await unsafeStore.AcknowledgeAuditAsync(Id("unsafe-lock-audit")));
        Assert.Equal(CredentialFailureCode.Unavailable, (await unsafeStore.AppendAsync(Evidence(Binding()), default)).Failure!.Code);
    }

    [Fact]
    public async Task Default_public_store_fails_closed_without_server_owned_trust_state()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);

        var read = await new CredentialRegistryStore(paths).ReadAsync();

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

        Assert.Equal(CredentialFailureCode.Unauthorized, invalid[1].Failure!.Code);
        Assert.All(invalid.Where((_, index) => index != 1), result =>
        {
            Assert.Equal(CredentialRegistryMutationStatus.Invalid, result.Status);
            Assert.Equal(CredentialFailureCode.InvalidRequest, result.Failure!.Code);
        });
        Assert.False(File.Exists(paths.CredentialRegistryDocumentPath));
    }

    [Fact]
    public async Task BindAndPostureCannotChangeImmutableProviderOrReferenceMetadata()
    {
        using var workspace = new TestWorkspace();
        var store = Store(new WorkspacePaths(workspace.RootPath));
        Assert.Equal(CredentialRegistryMutationStatus.Applied, (await store.MutateAsync(Register(0))).Status);
        Assert.True(CapabilityProviderId.TryParse("org.other", out var foreignProvider, out _));
        var foreignImplementation = Binding().Implementation with { ProviderId = foreignProvider! };
        var foreignBinding = Binding() with { Implementation = foreignImplementation, Scope = Binding().Scope with { Implementation = foreignImplementation } };
        var bind = new CredentialRegistryMutation(CredentialRegistryMutationKind.Bind, Id("bind-immutable"), 1, ReferenceId(), null, foreignBinding, null, null, null);
        var changedReference = Reference() with { Purpose = "Changed outside the lifecycle posture fields.", Status = CredentialLifecycleStatus.Disabled };
        var posture = new CredentialRegistryMutation(CredentialRegistryMutationKind.UpdatePosture, Id("posture-immutable"), 1, ReferenceId(), changedReference, null, null, CredentialProviderHealthStatus.Disabled, null);

        Assert.Equal(CredentialRegistryMutationStatus.Conflict, (await store.MutateAsync(bind)).Status);
        Assert.Equal(CredentialRegistryMutationStatus.Conflict, (await store.MutateAsync(posture)).Status);
        Assert.Equal(1, (await store.ReadAsync()).RegistryRevision);
    }

    [Fact]
    public async Task Undefined_registration_health_is_rejected_without_poisoning_later_valid_registration()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = Store(paths);

        var invalid = await store.MutateAsync(Register(0) with { Health = (CredentialProviderHealthStatus)999 });

        Assert.Equal(CredentialRegistryMutationStatus.Invalid, invalid.Status);
        Assert.Equal(CredentialFailureCode.InvalidRequest, invalid.Failure!.Code);
        Assert.False(File.Exists(paths.CredentialRegistryDocumentPath));
        Assert.False(File.Exists(paths.CredentialRegistryPrivateDocumentPath));
        Assert.False(File.Exists(paths.CredentialRegistryProofPath));
        Assert.False(File.Exists(paths.CredentialRegistryPrivateProofPath));

        var valid = await store.MutateAsync(Register(0));

        Assert.Equal(CredentialRegistryMutationStatus.Applied, valid.Status);
        var read = await store.ReadAsync();
        Assert.True(read.Succeeded);
        Assert.Equal(1, read.RegistryRevision);
        Assert.Equal(CredentialProviderHealthStatus.Available, Assert.Single(read.Entries).Health);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Invalid_returned_authentication_tags_fail_without_registry_artifacts(bool oversized)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var tag = oversized ? new string('a', 65) : string.Empty;
        var trust = new InvalidAuthenticationTagTrustProvider(TestTrust(paths), tag);
        var result = await new CredentialRegistryStore(paths, trust, new AcceptingLocatorVerifier()).MutateAsync(Register(0));

        Assert.Equal(CredentialRegistryMutationStatus.Unavailable, result.Status);
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

        Assert.Equal(CredentialRegistryMutationStatus.Applied, (await store.MutateAsync(Register(0))).Status);
        var found = await store.GetAsync(ReferenceId(), default);
        Assert.True(found.Succeeded);
        Assert.Equal(ReferenceId(), found.Reference!.Id);

        var binding = Binding();
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

    private sealed class RecordingLocatorVerifier : ICredentialProviderLocatorVerifier
    {
        public List<string> Locators { get; } = [];

        public ValueTask<bool> VerifyAsync(string workspaceIdentity, CredentialReferenceId referenceId, CredentialProviderId providerId, CredentialProviderLocator locator, CancellationToken cancellationToken)
        {
            Locators.Add(locator.Value);
            return ValueTask.FromResult(true);
        }
    }

    private sealed class LongAuthenticationTagTrustProvider(ICapabilityCatalogTrustProvider inner, int maximumAuthenticationTagUtf8Bytes) : ICapabilityCatalogTrustProvider
    {
        public int MaximumAuthenticationTagUtf8Bytes { get; } = maximumAuthenticationTagUtf8Bytes;
        public int InitializeCount { get; private set; }
        public int AuthenticateCount { get; private set; }

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
}
