using EmbodySense.Core.Application.Governance.Authority.Grants.Models;
using EmbodySense.Core.Application.Governance.Authority.Models;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Models;

namespace EmbodySense.Core.Application.Governance.Authority.Grants;

/// <summary>Resolves exact authority-profile pins over the authenticated authority-profile store.</summary>
public sealed class AuthorityGrantProfileSource : IAuthorityGrantProfileSource
{
    private readonly IAuthorityProfileStore _store;

    /// <summary>Creates an exact profile source over current authenticated profile truth.</summary>
    public AuthorityGrantProfileSource(IAuthorityProfileStore store) => _store = store ?? throw new ArgumentNullException(nameof(store));

    /// <inheritdoc />
    public async Task<AuthorityGrantProfileResolution> ResolveAsync(AuthorityGrantProfilePin? pin, DateTimeOffset evaluatedAtUtc, CancellationToken cancellationToken = default)
    {
        if (!IsValidPin(pin) || evaluatedAtUtc == default || evaluatedAtUtc.Offset != TimeSpan.Zero)
        {
            return Result(AuthorityGrantDependencyStatus.Invalid, null);
        }

        AuthorityProfileReadResult read;
        try
        {
            read = await _store.ReadAsync(pin!.Reference.ProfileId.Value, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Result(AuthorityGrantDependencyStatus.Unavailable, pin);
        }

        if (read is null)
        {
            return Result(AuthorityGrantDependencyStatus.Ambiguous, pin);
        }

        if (!Enum.IsDefined(read.Status))
        {
            return Result(AuthorityGrantDependencyStatus.Ambiguous, pin);
        }

        if (read.Status == AuthorityProfileReadStatus.Unavailable)
        {
            return Result(AuthorityGrantDependencyStatus.Unavailable, pin);
        }

        if (read.Status == AuthorityProfileReadStatus.NotFound && read.Record is null)
        {
            return Result(AuthorityGrantDependencyStatus.NotFound, pin);
        }

        if (read.Status != AuthorityProfileReadStatus.Available || !TryValidateRecord(read.Record, pin!, out var exact))
        {
            return Result(AuthorityGrantDependencyStatus.Ambiguous, pin);
        }

        var record = read.Record!;
        var causalHeadRecordedAtUtc = record.Tombstone?.RecordedAtUtc ?? record.Revisions[^1].RecordedAtUtc;
        if (causalHeadRecordedAtUtc > evaluatedAtUtc)
        {
            return Result(AuthorityGrantDependencyStatus.Ambiguous, pin);
        }

        if (exact is null)
        {
            return Result(AuthorityGrantDependencyStatus.NotFound, pin);
        }

        if (!record.CurrentProfile.ProfileId.Equals(pin!.Reference.ProfileId)
            || !record.CurrentProfile.Revision.Equals(pin.Reference.Revision)
            || !record.CurrentHash.Equals(pin.ContentHash))
        {
            return Resolved(AuthorityGrantDependencyStatus.Stale, pin, exact.Profile, record);
        }

        if (record.Tombstone is not null)
        {
            return Resolved(AuthorityGrantDependencyStatus.Disabled, pin, exact.Profile, record);
        }

        if (record.CurrentProfile.ExpiresAtUtc is { } expiry && expiry <= evaluatedAtUtc)
        {
            return Resolved(AuthorityGrantDependencyStatus.Expired, pin, exact.Profile, record);
        }

        if (record.CurrentProfile.Status != AuthorityProfileStatus.Active || evaluatedAtUtc < record.CurrentProfile.IssuedAtUtc)
        {
            return Resolved(AuthorityGrantDependencyStatus.Disabled, pin, exact.Profile, record);
        }

        return Resolved(AuthorityGrantDependencyStatus.Active, pin, exact.Profile, record);
    }

    private static bool TryValidateRecord(AuthorityProfileRecord? record, AuthorityGrantProfilePin pin, out AuthorityProfileRevisionEvidence? exact)
    {
        exact = null;
        if (record?.CurrentProfile is null
            || record.ProfileId is null
            || record.CurrentHash is null
            || record.Revisions is null
            || record.Operations is null
            || record.Revisions.Count is < 1 or > 128
            || record.Operations.Count is < 1 or > 4_096
            || !AuthorityProfileHash.TryCompute(record.CurrentProfile, out var currentHash, out var validation)
            || !validation.IsValid
            || !record.CurrentHash.Equals(currentHash)
            || !record.ProfileId.Equals(record.CurrentProfile.ProfileId))
        {
            return false;
        }

        var operationIds = new HashSet<string>(StringComparer.Ordinal);
        var revisionOperations = new Dictionary<int, AuthorityProfileOperationReceipt>();
        AuthorityProfileOperationReceipt? tombstoneOperation = null;
        foreach (var operation in record.Operations)
        {
            if (operation is null
                || !IsToken(operation.OperationId)
                || !operationIds.Add(operation.OperationId)
                || !IsSha256(operation.RequestHash)
                || !Enum.IsDefined(operation.Kind)
                || operation.Outcome != AuthorityProfileMutationStatus.Applied
                || operation.ProfileId is null
                || !operation.ProfileId.Equals(record.ProfileId)
                || operation.ActorId is null
                || operation.Reason is null
                || operation.RecordedAtUtc == default
                || operation.RecordedAtUtc.Offset != TimeSpan.Zero)
            {
                return false;
            }

            if (operation.Kind == AuthorityProfileMutationKind.Tombstone)
            {
                if (operation.ResultingRevision is not null || tombstoneOperation is not null)
                {
                    return false;
                }

                tombstoneOperation = operation;
                continue;
            }

            if (operation.ResultingRevision is not { } resultingRevision
                || resultingRevision is < 1 or > 128
                || !revisionOperations.TryAdd(resultingRevision, operation))
            {
                return false;
            }
        }

        DateTimeOffset? previousRevisionTime = null;
        for (var index = 0; index < record.Revisions.Count; index++)
        {
            var revision = record.Revisions[index];
            var revisionNumber = index + 1;
            if (revision?.Profile is null
                || revision.Profile.ProfileId is null
                || revision.Profile.Revision is null
                || revision.Hash is null
                || revision.Profile.Revision.Value != revisionNumber
                || !revision.Profile.ProfileId.Equals(record.ProfileId)
                || !IsToken(revision.OperationId)
                || revision.RecordedAtUtc == default
                || revision.RecordedAtUtc.Offset != TimeSpan.Zero
                || previousRevisionTime is { } previous && revision.RecordedAtUtc < previous
                || !AuthorityProfileHash.TryCompute(revision.Profile, out var hash, out var revisionValidation)
                || !revisionValidation.IsValid
                || !revision.Hash.Equals(hash))
            {
                return false;
            }

            if (!revisionOperations.TryGetValue(revisionNumber, out var operation)
                || !string.Equals(operation.OperationId, revision.OperationId, StringComparison.Ordinal)
                || operation.RecordedAtUtc != revision.RecordedAtUtc
                || revisionNumber == 1 && operation.Kind != AuthorityProfileMutationKind.Create
                || revisionNumber > 1 && operation.Kind is not AuthorityProfileMutationKind.Revise and not AuthorityProfileMutationKind.TransitionStatus
                || operation.Kind == AuthorityProfileMutationKind.TransitionStatus
                    && !IsPostureOnlyTransition(record.Revisions[index - 1].Profile, revision.Profile))
            {
                return false;
            }

            if (revision.Profile.ProfileId.Equals(pin.Reference.ProfileId)
                && revision.Profile.Revision.Equals(pin.Reference.Revision)
                && revision.Hash.Equals(pin.ContentHash))
            {
                exact = revision;
            }

            previousRevisionTime = revision.RecordedAtUtc;
        }

        if (!Equals(record.CurrentProfile, record.Revisions[^1].Profile)
            || !record.CurrentHash.Equals(record.Revisions[^1].Hash))
        {
            return false;
        }

        if (revisionOperations.Count != record.Revisions.Count
            || record.Operations.Count != record.Revisions.Count + (record.Tombstone is null ? 0 : 1))
        {
            return false;
        }

        if (record.Tombstone is null)
        {
            return tombstoneOperation is null;
        }

        var tombstone = record.Tombstone;
        if (tombstoneOperation is null
            || !IsToken(tombstone.OperationId)
            || tombstone.ActorId is null
            || tombstone.Reason is null
            || tombstone.RecordedAtUtc == default
            || tombstone.RecordedAtUtc.Offset != TimeSpan.Zero
            || !string.Equals(tombstoneOperation.OperationId, tombstone.OperationId, StringComparison.Ordinal)
            || !tombstoneOperation.ActorId.Equals(tombstone.ActorId)
            || !tombstoneOperation.Reason.Equals(tombstone.Reason)
            || tombstone.RecordedAtUtc < record.Revisions[^1].RecordedAtUtc
            || tombstoneOperation.RecordedAtUtc != tombstone.RecordedAtUtc)
        {
            return false;
        }

        return true;
    }

    private static bool IsPostureOnlyTransition(AuthorityProfile previous, AuthorityProfile current)
        => previous.SchemaVersion == current.SchemaVersion
            && previous.ProfileId.Equals(current.ProfileId)
            && current.Revision.Value == previous.Revision.Value + 1
            && Equals(previous.Purpose, current.Purpose)
            && Equals(previous.Provenance, current.Provenance)
            && previous.IssuedAtUtc == current.IssuedAtUtc
            && previous.ExpiresAtUtc == current.ExpiresAtUtc
            && AuthorityCeilingSubset.IsEqual(previous.Ceiling, current.Ceiling)
            && previous.BoundaryConditions.SequenceEqual(current.BoundaryConditions);

    private static AuthorityGrantProfileResolution Resolved(AuthorityGrantDependencyStatus status, AuthorityGrantProfilePin pin, AuthorityProfile profile, AuthorityProfileRecord record)
    {
        var operationId = record.Tombstone?.OperationId ?? record.Revisions[^1].OperationId;
        var evidence = AuthorityGrantEvidenceHash.Compute(pin.Reference.ProfileId.Value, pin.Reference.Revision.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), pin.ContentHash.Value, record.CurrentHash.Value, operationId);
        return new AuthorityGrantProfileResolution(status, pin, profile, evidence);
    }

    private static AuthorityGrantProfileResolution Result(AuthorityGrantDependencyStatus status, AuthorityGrantProfilePin? pin)
        => new(status, pin, null, string.Empty);

    private static bool IsValidPin(AuthorityGrantProfilePin? pin)
        => pin?.Reference?.ProfileId is not null
            && pin.Reference.Revision is not null
            && pin.ContentHash is not null
            && AuthorityProfileId.TryParse(pin.Reference.ProfileId.Value, out _, out _)
            && AuthorityProfileRevision.TryParse(pin.Reference.Revision.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), out _, out _)
            && AuthorityProfileHash.TryParse(pin.ContentHash.Value, out _, out _);

    private static bool IsToken(string? value)
        => value is { Length: > 0 and <= 128 }
            && value[0] is >= 'a' and <= 'z' or >= '0' and <= '9'
            && value[^1] is >= 'a' and <= 'z' or >= '0' and <= '9'
            && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '.');

    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
