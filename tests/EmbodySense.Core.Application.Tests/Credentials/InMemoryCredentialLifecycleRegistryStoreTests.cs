using EmbodySense.Core.Application.Credentials;
using EmbodySense.Core.Application.Credentials.Models;
using EmbodySense.Core.Common.Credentials;
using EmbodySense.Core.Common.Credentials.Models;
using EmbodySense.Core.Common.Governance.Audit;

namespace EmbodySense.Core.Application.Tests.Credentials;

public sealed class InMemoryCredentialLifecycleRegistryStoreTests
{
    private const string ActorId = "user-1";
    private const string WorkspaceId = "workspace-1";

    [Fact]
    public async Task Exact_unresolved_repair_intent_allows_confirmed_reconciliation_outcome()
    {
        var (store, mutation) = await PreparedReconciliationAsync();

        var result = await store.MutateAsync(mutation);

        Assert.Equal(CredentialRegistryMutationStatus.Applied, result.Status);
    }

    [Theory]
    [InlineData("missing-audit")]
    [InlineData("missing-preview")]
    [InlineData("wrong-intent")]
    [InlineData("stale-revision")]
    [InlineData("wrong-actor")]
    [InlineData("wrong-workspace")]
    [InlineData("wrong-phase")]
    public async Task Reconciliation_rejects_any_mutation_without_the_exact_closed_adapter_contract(string variation)
    {
        var (store, mutation) = await PreparedReconciliationAsync();
        mutation = variation switch
        {
            "missing-audit" => mutation with { LifecycleAudit = null },
            "missing-preview" => mutation with { PreviewHash = null },
            "wrong-intent" => mutation with { LifecycleIntentOperationId = Id("other-intent") },
            "stale-revision" => mutation with { ExpectedRegistryRevision = 0 },
            "wrong-actor" => mutation with { ActorId = "user-2" },
            "wrong-workspace" => mutation with { WorkspaceId = "workspace-2" },
            "wrong-phase" => mutation with { LifecyclePhase = CredentialLifecycleMutationPhase.RepairComplete },
            _ => throw new ArgumentOutOfRangeException(nameof(variation))
        };

        var result = await store.MutateAsync(mutation);

        Assert.Equal(CredentialRegistryMutationStatus.Invalid, result.Status);
        Assert.Equal(CredentialFailureCode.Unauthorized, result.Failure?.Code);
    }

    private static async Task<(InMemoryCredentialLifecycleRegistryStore Store, CredentialRegistryMutation Reconciliation)> PreparedReconciliationAsync()
    {
        var store = new InMemoryCredentialLifecycleRegistryStore(ActorId, DateTimeOffset.UnixEpoch);
        var referenceId = ReferenceId();
        var intentId = Id("repair-intent");
        var intent = new CredentialRegistryMutation(CredentialRegistryMutationKind.BeginRepair, intentId, 0, referenceId, null, null, null, null, null, LifecycleOperation: (int)CredentialLifecycleOperationKind.Repair, ActorId: ActorId, PreviewHash: Hash('a'), LifecycleRequestHash: Hash('b'), LifecyclePhase: CredentialLifecycleMutationPhase.Intent, LifecycleIntentOperationId: intentId, WorkspaceId: WorkspaceId);
        Assert.Equal(CredentialRegistryMutationStatus.Applied, (await store.MutateAsync(intent)).Status);

        var reconciliationId = Id("repair-reconciliation");
        var reconciliation = new CredentialRegistryMutation(CredentialRegistryMutationKind.ReconcileRepair, reconciliationId, 1, referenceId, null, null, null, null, null, LifecycleOperation: (int)CredentialLifecycleOperationKind.ReconcileRepair, ActorId: ActorId, PreviewHash: Hash('c'), LifecycleRequestHash: Hash('d'), LifecyclePhase: CredentialLifecycleMutationPhase.RepairReconciledUncertain, LifecycleIntentOperationId: intentId, WorkspaceId: WorkspaceId, LifecycleAudit: new CredentialLifecycleAuditPayload(AuditSchema.Actions.CredentialLifecycleOutcome, AuditSchema.Outcomes.Failed, "Credential repair reconciliation remains uncertain."));
        return (store, reconciliation);
    }

    private static CredentialContractId Id(string value) => CredentialContractId.TryParse(value, out var parsed, out _) ? parsed! : throw new InvalidOperationException("The test contract id is invalid.");

    private static CredentialReferenceId ReferenceId() => CredentialReferenceId.TryParse("credential-1", out var parsed, out _) ? parsed! : throw new InvalidOperationException("The test reference id is invalid.");

    private static string Hash(char value) => "sha256:" + new string(value, 64);
}
