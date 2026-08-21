using EmbodySense.Core.Application.Credentials;
using EmbodySense.Core.Application.Credentials.Models;
using EmbodySense.Core.Common.Credentials;
using EmbodySense.Core.Common.Credentials.Leases.Models;
using EmbodySense.Core.Common.Credentials.Models;
using EmbodySense.Core.Common.Governance.Audit;

namespace EmbodySense.Core.Application.Tests.Credentials;

/// <summary>Provides an in-memory implementation of the public lifecycle registry contract for Application behavior tests.</summary>
internal sealed class InMemoryCredentialLifecycleRegistryStore : ICredentialRegistryStore
{
    private static readonly CredentialContractHash _operationHash = ParseHash("sha256:0000000000000000000000000000000000000000000000000000000000000000");
    private readonly string _authenticatedActorId;
    private readonly DateTimeOffset _timestamp;
    private readonly List<CredentialRegistryEntry> _entries = [];
    private readonly List<CredentialRegistryTombstone> _tombstones = [];
    private readonly List<CredentialRegistryOperationEvidence> _operations = [];
    private readonly List<CredentialUseEvidence> _evidence = [];
    private readonly List<CredentialLifecycleAuditOutboxItem> _pendingAudits = [];
    private bool _available = true;
    private long _revision;

    internal InMemoryCredentialLifecycleRegistryStore(string authenticatedActorId, DateTimeOffset timestamp)
    {
        _authenticatedActorId = authenticatedActorId;
        _timestamp = timestamp;
    }

    public ValueTask<CredentialActorAuthentication> AuthenticateActorAsync(string actorId, CancellationToken cancellationToken) => ValueTask.FromResult(string.Equals(actorId, _authenticatedActorId, StringComparison.Ordinal) ? CredentialActorAuthentication.AuthenticatedUser : CredentialActorAuthentication.Unauthenticated);

    public ValueTask<CredentialReferenceLookupResult> GetAsync(CredentialReferenceId referenceId, CancellationToken cancellationToken)
    {
        if (!_available)
        {
            return ValueTask.FromResult(CredentialReferenceLookupResult.Failed(CredentialFailure.FromCode(CredentialFailureCode.Unavailable)));
        }

        var entry = _entries.SingleOrDefault(candidate => candidate.Reference.Id.Equals(referenceId));
        return ValueTask.FromResult(entry is null ? CredentialReferenceLookupResult.Failed(CredentialFailure.FromCode(CredentialFailureCode.NotFound)) : CredentialReferenceLookupResult.Found(entry.Reference));
    }

    public Task<CredentialRegistryReadResult> ReadAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateReadResult(_available));

    internal Task<CredentialRegistryReadResult> ReadDiagnosticAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateReadResult(true));

    public Task<CredentialRegistryMutationResult> MutateAsync(CredentialRegistryMutation mutation, CancellationToken cancellationToken = default)
    {
        if (!_available)
        {
            return Task.FromResult(Result(CredentialRegistryMutationStatus.Unavailable, mutation, null, CredentialFailureCode.Unavailable));
        }
        if (mutation.Kind == CredentialRegistryMutationKind.ReconcileRepair && !CanReconcileRepair(mutation))
        {
            return Task.FromResult(Result(CredentialRegistryMutationStatus.Invalid, mutation, null, CredentialFailureCode.Unauthorized));
        }

        var prior = _operations.SingleOrDefault(operation => operation.OperationId.Equals(mutation.OperationId));
        if (prior is not null)
        {
            return Task.FromResult(Matches(prior, mutation) ? Result(CredentialRegistryMutationStatus.Replayed, mutation, FindEntry(mutation.ReferenceId), null) : Result(CredentialRegistryMutationStatus.Conflict, mutation, null, CredentialFailureCode.Conflict));
        }
        if (mutation.ExpectedRegistryRevision != _revision)
        {
            return Task.FromResult(Result(CredentialRegistryMutationStatus.Conflict, mutation, null, CredentialFailureCode.Conflict));
        }

        var entry = FindEntry(mutation.ReferenceId);
        if (!Apply(mutation, ref entry))
        {
            return Task.FromResult(Result(CredentialRegistryMutationStatus.Conflict, mutation, null, CredentialFailureCode.Conflict));
        }

        _revision++;
        var evidence = new CredentialRegistryOperationEvidence(mutation.OperationId, _operationHash, (int)mutation.Kind, _revision, mutation.ReferenceId, mutation.LifecycleOperation, mutation.ActorId, mutation.PreviewHash, mutation.LifecycleRequestHash, mutation.LifecyclePhase, mutation.LifecycleIntentOperationId, mutation.Health, mutation.AffectedActiveRuns, mutation.WorkspaceId);
        _operations.Add(evidence);
        if (mutation.LifecycleAudit is not null)
        {
            _pendingAudits.Add(new CredentialLifecycleAuditOutboxItem(mutation.OperationId, mutation.LifecycleIntentOperationId ?? mutation.OperationId, mutation.ReferenceId, mutation.WorkspaceId!, mutation.ActorId!, (CredentialLifecycleOperationKind)mutation.LifecycleOperation!, _timestamp, _revision, mutation.PreviewHash, mutation.LifecycleAudit.Action, mutation.LifecycleAudit.Outcome, mutation.LifecycleAudit.Detail));
        }
        return Task.FromResult(Result(CredentialRegistryMutationStatus.Applied, mutation, entry, null));
    }

    public Task<bool> AcknowledgeAuditAsync(CredentialContractId auditOperationId, CancellationToken cancellationToken = default)
    {
        if (!_available)
        {
            return Task.FromResult(false);
        }

        var item = _pendingAudits.SingleOrDefault(candidate => candidate.AuditOperationId.Equals(auditOperationId));
        if (item is null)
        {
            return Task.FromResult(false);
        }

        _pendingAudits.Remove(item);
        return Task.FromResult(true);
    }

    public ValueTask<CredentialEvidenceWriteResult> AppendAsync(CredentialUseEvidence evidence, CancellationToken cancellationToken)
    {
        if (!_available)
        {
            return ValueTask.FromResult(CredentialEvidenceWriteResult.Failed(CredentialFailure.FromCode(CredentialFailureCode.Unavailable)));
        }

        _evidence.Add(evidence);
        return ValueTask.FromResult(CredentialEvidenceWriteResult.Success());
    }

    public ValueTask<CredentialEvidenceWriteResult> ReserveAsync(CredentialLeaseIntent intent, CancellationToken cancellationToken)
        => ValueTask.FromResult(_available ? CredentialEvidenceWriteResult.Success() : CredentialEvidenceWriteResult.Failed(CredentialFailure.FromCode(CredentialFailureCode.Unavailable)));

    internal void MakeUnavailable() => _available = false;

    private bool Apply(CredentialRegistryMutation mutation, ref CredentialRegistryEntry? entry)
    {
        switch (mutation.Kind)
        {
            case CredentialRegistryMutationKind.BeginCreate:
            case CredentialRegistryMutationKind.BeginRepair:
            case CredentialRegistryMutationKind.RecordLocatorUncertain:
            case CredentialRegistryMutationKind.RecordRepairUncertain:
            case CredentialRegistryMutationKind.ReconcileRepair:
                return true;
            case CredentialRegistryMutationKind.Register:
                if (entry is not null || mutation.Reference is null || mutation.Binding is null || mutation.ConsentReference is null || mutation.Health is null || !CredentialContractJson.TryHash(mutation.Binding, out var bindingHash, out _))
                {
                    return false;
                }

                entry = new CredentialRegistryEntry(mutation.Reference, mutation.Binding, bindingHash!, mutation.ConsentReference, mutation.Health.Value, _revision + 1, mutation.OperationId, mutation.ConsentGranted ?? false);
                _entries.Add(entry);
                return true;
            case CredentialRegistryMutationKind.SetHealth:
                entry = entry is null ? null : entry with { Health = mutation.Health ?? entry.Health, Revision = _revision + 1, LastOperationId = mutation.OperationId };
                return ReplaceEntry(FindEntry(mutation.ReferenceId), entry);
            case CredentialRegistryMutationKind.Bind:
                if (entry is null || mutation.Binding is null || !CredentialContractJson.TryHash(mutation.Binding, out var updatedBindingHash, out _))
                {
                    return false;
                }

                entry = entry with { Binding = mutation.Binding, BindingHash = updatedBindingHash!, Revision = _revision + 1, LastOperationId = mutation.OperationId };
                return ReplaceEntry(FindEntry(mutation.ReferenceId), entry);
            case CredentialRegistryMutationKind.Consent:
                entry = entry is null ? null : entry with { ConsentReference = mutation.ConsentReference ?? entry.ConsentReference, ConsentGranted = mutation.ConsentGranted ?? entry.ConsentGranted, Revision = _revision + 1, LastOperationId = mutation.OperationId };
                return ReplaceEntry(FindEntry(mutation.ReferenceId), entry);
            case CredentialRegistryMutationKind.UpdatePosture:
                if (entry is null || mutation.Reference is null)
                {
                    return false;
                }

                entry = entry with { Reference = mutation.Reference, Health = mutation.Health ?? entry.Health, Revision = _revision + 1, LastOperationId = mutation.OperationId };
                return ReplaceEntry(FindEntry(mutation.ReferenceId), entry);
            case CredentialRegistryMutationKind.Tombstone:
                if (entry is null || !CredentialContractJson.TryHash(entry.Reference, out var referenceHash, out _))
                {
                    return false;
                }

                _entries.Remove(entry);
                var needsRepair = mutation.LifecyclePhase is CredentialLifecycleMutationPhase.TombstoneUncertain or CredentialLifecycleMutationPhase.RepairUncertain;
                _tombstones.RemoveAll(candidate => candidate.ReferenceId.Equals(mutation.ReferenceId));
                _tombstones.Add(new CredentialRegistryTombstone(mutation.ReferenceId, _revision + 1, mutation.OperationId, _timestamp, referenceHash!, needsRepair, needsRepair ? entry.Binding : null, needsRepair ? entry.Reference.ProviderId : null));
                entry = null;
                return true;
            case CredentialRegistryMutationKind.CompleteRepair:
                var tombstone = _tombstones.SingleOrDefault(candidate => candidate.ReferenceId.Equals(mutation.ReferenceId));
                if (tombstone is null)
                {
                    return false;
                }

                _tombstones[_tombstones.IndexOf(tombstone)] = tombstone with { NeedsRepair = false, Revision = _revision + 1, OperationId = mutation.OperationId };
                return true;
            default:
                return false;
        }
    }

    private bool ReplaceEntry(CredentialRegistryEntry? current, CredentialRegistryEntry? replacement)
    {
        if (current is null || replacement is null)
        {
            return false;
        }

        _entries[_entries.IndexOf(current)] = replacement;
        return true;
    }

    private CredentialRegistryReadResult CreateReadResult(bool succeeded) => new(succeeded ? _revision : null, _entries.ToArray(), _tombstones.ToArray(), _operations.ToArray(), _evidence.ToArray(), succeeded ? null : CredentialFailure.FromCode(CredentialFailureCode.Unavailable), _pendingAudits.ToArray());

    private CredentialRegistryEntry? FindEntry(CredentialReferenceId referenceId) => _entries.SingleOrDefault(candidate => candidate.Reference.Id.Equals(referenceId));

    private CredentialRegistryMutationResult Result(CredentialRegistryMutationStatus status, CredentialRegistryMutation mutation, CredentialRegistryEntry? entry, CredentialFailureCode? failure) => new(status, mutation.OperationId, _revision, entry, failure is null ? null : CredentialFailure.FromCode(failure.Value));

    private bool CanReconcileRepair(CredentialRegistryMutation mutation)
    {
        var interrupted = _operations.SingleOrDefault(operation => mutation.LifecycleIntentOperationId?.Equals(operation.OperationId) == true);
        var terminalExists = interrupted is not null && _operations.Any(operation => operation.LifecycleIntentOperationId?.Equals(interrupted.OperationId) == true && operation.LifecyclePhase is CredentialLifecycleMutationPhase.RepairComplete or CredentialLifecycleMutationPhase.RepairUncertain or CredentialLifecycleMutationPhase.RepairReconciledUncertain);
        var exactDurableIntent = mutation.ExpectedRegistryRevision == _revision && interrupted is not null && interrupted.Kind == (int)CredentialRegistryMutationKind.BeginRepair && interrupted.LifecyclePhase == CredentialLifecycleMutationPhase.Intent && interrupted.ReferenceId.Equals(mutation.ReferenceId) && string.Equals(interrupted.WorkspaceId, mutation.WorkspaceId, StringComparison.Ordinal) && string.Equals(interrupted.ActorId, mutation.ActorId, StringComparison.Ordinal) && !terminalExists;
        var exactConfirmedOutcome = mutation.LifecycleOperation == (int)CredentialLifecycleOperationKind.ReconcileRepair && mutation.LifecyclePhase == CredentialLifecycleMutationPhase.RepairReconciledUncertain && mutation.PreviewHash is not null && mutation.LifecycleRequestHash is not null && mutation.LifecycleAudit is { Action: AuditSchema.Actions.CredentialLifecycleOutcome, Outcome: AuditSchema.Outcomes.Failed };
        return exactDurableIntent && exactConfirmedOutcome;
    }

    private static bool Matches(CredentialRegistryOperationEvidence evidence, CredentialRegistryMutation mutation) => evidence.Kind == (int)mutation.Kind && string.Equals(evidence.LifecycleRequestHash, mutation.LifecycleRequestHash, StringComparison.Ordinal) && evidence.LifecyclePhase == mutation.LifecyclePhase;

    private static CredentialContractHash ParseHash(string value) => CredentialContractHash.TryParse(value, out var parsed, out _) ? parsed! : throw new InvalidOperationException("The test operation hash is invalid.");
}
