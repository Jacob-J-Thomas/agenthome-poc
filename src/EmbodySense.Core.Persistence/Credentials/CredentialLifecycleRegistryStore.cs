using EmbodySense.Core.Application.Credentials;
using EmbodySense.Core.Application.Credentials.Models;
using EmbodySense.Core.Common.Credentials;
using EmbodySense.Core.Common.Credentials.Leases.Models;
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

    public Task<CredentialRegistryMutationResult> MutateAsync(CredentialRegistryMutation mutation, CancellationToken cancellationToken = default)
    {
        if (mutation?.ReferenceId is not null && !string.IsNullOrEmpty(mutation.WorkspaceId))
        {
            var target = CredentialProviderTarget.Derive(mutation.WorkspaceId, mutation.ReferenceId);
            if (!CredentialOperationMutex.TryAcquire(target, cancellationToken, out var operationLock))
            {
                return Task.FromResult(new CredentialRegistryMutationResult(CredentialRegistryMutationStatus.Unavailable, mutation.OperationId, null, null, CredentialFailure.FromCode(CredentialFailureCode.Unavailable)));
            }

            using (operationLock)
            {
                // Named mutex ownership is thread-affine. Complete the bounded durable mutation on the acquiring thread.
                var result = MutateOrderedAsync(mutation, cancellationToken).ConfigureAwait(false).GetAwaiter().GetResult();
                return Task.FromResult(result);
            }
        }

        return MutateOrderedAsync(mutation!, cancellationToken);
    }

    private async Task<CredentialRegistryMutationResult> MutateOrderedAsync(CredentialRegistryMutation mutation, CancellationToken cancellationToken)
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

    public Task<bool> AcknowledgeAuditAsync(CredentialContractId auditOperationId, CancellationToken cancellationToken = default) => registry.AcknowledgeLifecycleAuditAsync(auditOperationId, cancellationToken);

    public ValueTask<CredentialEvidenceWriteResult> ReserveAsync(CredentialLeaseIntent intent, CancellationToken cancellationToken) => registry.ReserveAsync(intent, cancellationToken);

    public ValueTask<CredentialEvidenceWriteResult> AppendAsync(CredentialUseEvidence evidence, CancellationToken cancellationToken) => registry.AppendAsync(evidence, cancellationToken);
}
