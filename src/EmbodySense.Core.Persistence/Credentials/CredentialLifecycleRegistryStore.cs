using EmbodySense.Core.Application.Credentials;
using EmbodySense.Core.Application.Credentials.Models;
using EmbodySense.Core.Common.Credentials;
using EmbodySense.Core.Common.Credentials.Models;
using EmbodySense.Core.Common.Governance.Audit;

namespace EmbodySense.Core.Persistence.Credentials;

internal sealed class CredentialLifecycleRegistryStore(CredentialRegistryStore registry) : ICredentialRegistryStore
{
    public ValueTask<CredentialActorAuthentication> AuthenticateActorAsync(string actorId, CancellationToken cancellationToken)
    {
        var authentication = string.Equals(actorId, Environment.UserName, StringComparison.Ordinal) ? CredentialActorAuthentication.AuthenticatedUser : CredentialActorAuthentication.Unauthenticated;
        return ValueTask.FromResult(authentication);
    }

    public ValueTask<CredentialReferenceLookupResult> GetAsync(CredentialReferenceId referenceId, CancellationToken cancellationToken) => registry.GetAsync(referenceId, cancellationToken);

    public Task<CredentialRegistryReadResult> ReadAsync(CancellationToken cancellationToken = default) => registry.ReadAsync(cancellationToken);

    public async Task<CredentialRegistryMutationResult> MutateAsync(CredentialRegistryMutation mutation, CancellationToken cancellationToken = default)
    {
        if (mutation?.Kind != CredentialRegistryMutationKind.ReconcileRepair)
        {
            return await registry.MutateLifecycleAsync(mutation!, cancellationToken);
        }

        var read = await registry.ReadAsync(cancellationToken);
        var interrupted = read.Operations.SingleOrDefault(operation => mutation.LifecycleIntentOperationId?.Equals(operation.OperationId) == true);
        var terminalExists = interrupted is not null && read.Operations.Any(operation => operation.LifecycleIntentOperationId?.Equals(interrupted.OperationId) == true && operation.LifecyclePhase is CredentialLifecycleMutationPhase.RepairComplete or CredentialLifecycleMutationPhase.RepairUncertain or CredentialLifecycleMutationPhase.RepairReconciledUncertain);
        var exactDurableIntent = read.Succeeded && read.RegistryRevision == mutation.ExpectedRegistryRevision && interrupted is not null && interrupted.Kind == (int)CredentialRegistryMutationKind.BeginRepair && interrupted.LifecyclePhase == CredentialLifecycleMutationPhase.Intent && interrupted.ReferenceId.Equals(mutation.ReferenceId) && string.Equals(interrupted.WorkspaceId, mutation.WorkspaceId, StringComparison.Ordinal) && string.Equals(interrupted.ActorId, mutation.ActorId, StringComparison.Ordinal) && !terminalExists;
        var exactConfirmedOutcome = mutation.LifecycleOperation == (int)CredentialLifecycleOperationKind.ReconcileRepair && mutation.LifecyclePhase == CredentialLifecycleMutationPhase.RepairReconciledUncertain && mutation.PreviewHash is not null && mutation.LifecycleRequestHash is not null && mutation.LifecycleAudit is { Action: AuditSchema.Actions.CredentialLifecycleOutcome, Outcome: AuditSchema.Outcomes.Failed };
        if (!exactDurableIntent || !exactConfirmedOutcome)
        {
            return new CredentialRegistryMutationResult(CredentialRegistryMutationStatus.Invalid, mutation.OperationId, read.RegistryRevision, null, CredentialFailure.FromCode(CredentialFailureCode.Unauthorized));
        }

        return await registry.MutateLifecycleAsync(mutation, cancellationToken);
    }

    public Task<bool> AcknowledgeAuditAsync(CredentialContractId auditOperationId, CancellationToken cancellationToken = default) => registry.AcknowledgeAuditAsync(auditOperationId, cancellationToken);

    public ValueTask<CredentialEvidenceWriteResult> AppendAsync(CredentialUseEvidence evidence, CancellationToken cancellationToken) => registry.AppendAsync(evidence, cancellationToken);
}
