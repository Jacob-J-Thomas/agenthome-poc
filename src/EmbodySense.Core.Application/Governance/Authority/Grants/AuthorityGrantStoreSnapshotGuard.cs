using EmbodySense.Core.Application.Governance.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Grants.Models;

namespace EmbodySense.Core.Application.Governance.Authority.Grants;

internal static class AuthorityGrantStoreSnapshotGuard
{
    internal static bool TryCapture(AuthorityGrantStoreSnapshot? candidate, AuthorityGrantId grantId, long storeGeneration, out AuthorityGrantStoreSnapshot? snapshot)
    {
        snapshot = null;
        if (candidate?.CurrentGrant is null
            || candidate.Revisions is null
            || candidate.Operations is null
            || storeGeneration < 1
            || candidate.Revisions.Count is < 1 or > AuthorityGrantContractLimits.MaxRevisionsPerGrant
            || candidate.Operations.Count < candidate.Revisions.Count
            || candidate.Operations.Count > AuthorityGrantContractLimits.MaxOperationsPerStore
            || storeGeneration < candidate.Operations.Count)
        {
            return false;
        }

        var revisions = candidate.Revisions.ToArray();
        var operations = candidate.Operations.ToArray();
        var revisionIndex = 0;
        DateTimeOffset? previousOperationTime = null;
        var operationIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var operation in operations)
        {
            if (operation is null
                || !AuthorityGrantContractValidator.Validate(operation).IsValid
                || !grantId.Equals(operation.GrantId)
                || operation.ExpectedRevision is < 0 or > int.MaxValue
                || !operationIds.Add(operation.OperationId)
                || previousOperationTime is { } previous && operation.RecordedAtUtc < previous)
            {
                return false;
            }

            previousOperationTime = operation.RecordedAtUtc;
            if (operation.Outcome != AuthorityGrantOperationOutcome.Committed)
            {
                var current = revisionIndex == 0 ? null : revisions[revisionIndex - 1];
                if (!IsValidReceiptAtState(operation, current))
                {
                    return false;
                }

                continue;
            }

            if (revisionIndex >= revisions.Length)
            {
                return false;
            }

            var revision = revisions[revisionIndex];
            if (revision is null
                || !AuthorityGrantContractValidator.Validate(revision).IsValid
                || !grantId.Equals(revision.GrantId)
                || revision.Revision.Value != revisionIndex + 1
                || operation.ExpectedRevision != revisionIndex
                || operation.ResultingGrant is null
                || !Matches(operation.ResultingGrant, revision)
                || !Equals(operation.ActorId, revision.ChangedByActorId)
                || !Equals(operation.Reason, revision.Reason)
                || operation.RecordedAtUtc != revision.RecordedAtUtc)
            {
                return false;
            }

            if (revisionIndex == 0)
            {
                if (operation.Kind != AuthorityGrantOperationKind.Create || revision.PredecessorRevision is not null || revision.PredecessorContentHash is not null)
                {
                    return false;
                }
            }
            else if (!AuthorityGrantContractValidator.ValidateTransition(revisions[revisionIndex - 1], revision, operation.Kind).IsValid)
            {
                return false;
            }

            revisionIndex++;
        }

        if (revisionIndex != revisions.Length || !Equals(candidate.CurrentGrant, revisions[^1]))
        {
            return false;
        }

        snapshot = new AuthorityGrantStoreSnapshot(candidate.CurrentGrant, Array.AsReadOnly(revisions), Array.AsReadOnly(operations));
        return true;
    }

    private static bool IsValidReceiptAtState(AuthorityGrantOperationEvidence operation, AuthorityGrant? current)
    {
        var exactNonterminal = current is not null
            && operation.ExpectedRevision == current.Revision.Value
            && current.Status is AuthorityGrantLifecycleStatus.Active or AuthorityGrantLifecycleStatus.Suspended;
        return (operation.Outcome, operation.FailureCode, operation.Kind) switch
        {
            (AuthorityGrantOperationOutcome.NotFound, AuthorityGrantOperationFailureCode.LifecycleConflict, not AuthorityGrantOperationKind.Create)
                => current is null,
            (AuthorityGrantOperationOutcome.Conflict, AuthorityGrantOperationFailureCode.LifecycleConflict, _)
                => current is not null,
            (AuthorityGrantOperationOutcome.Conflict, AuthorityGrantOperationFailureCode.BoundaryConflict, AuthorityGrantOperationKind.Create)
                => current is null,
            (AuthorityGrantOperationOutcome.Conflict, AuthorityGrantOperationFailureCode.BoundaryConflict, AuthorityGrantOperationKind.Replace or AuthorityGrantOperationKind.Expire)
                => exactNonterminal,
            (AuthorityGrantOperationOutcome.Conflict, AuthorityGrantOperationFailureCode.CeilingExceeded, AuthorityGrantOperationKind.Create)
                => current is null,
            (AuthorityGrantOperationOutcome.Conflict, AuthorityGrantOperationFailureCode.CeilingExceeded, AuthorityGrantOperationKind.Narrow or AuthorityGrantOperationKind.Replace)
                => exactNonterminal,
            (AuthorityGrantOperationOutcome.LimitExceeded, AuthorityGrantOperationFailureCode.LimitExceeded, _)
                => true,
            _ => false,
        };
    }

    internal static AuthorityGrant? Find(AuthorityGrantStoreSnapshot snapshot, AuthorityGrantReference reference)
        => snapshot.Revisions.SingleOrDefault(grant => Matches(reference, grant));

    internal static bool Contains(AuthorityGrantStoreSnapshot snapshot, AuthorityGrantOperationEvidence evidence)
        => snapshot.Operations.Any(candidate => Equals(candidate, evidence));

    internal static bool Matches(AuthorityGrantReference reference, AuthorityGrant grant)
        => reference.GrantId.Equals(grant.GrantId)
            && reference.Revision.Equals(grant.Revision)
            && string.Equals(reference.ContentHash, grant.ContentHash, StringComparison.Ordinal);
}
