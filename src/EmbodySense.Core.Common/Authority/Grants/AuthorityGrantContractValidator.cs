using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.Loops.Revisions;

namespace EmbodySense.Core.Common.Authority.Grants;

/// <summary>Validates bounded schema-version-1 authority grants, evidence, and immutable lifecycle transitions.</summary>
public static class AuthorityGrantContractValidator
{
    /// <summary>Validates one complete immutable grant revision, including its canonical content hash.</summary>
    public static AuthorityGrantValidationResult Validate(AuthorityGrant? grant)
    {
        var errors = ValidateStructure(grant);
        if (grant is not null && (!AuthorityGrantHash.IsCanonical(grant.ContentHash) || !AuthorityGrantHash.Matches(grant)))
        {
            Add(errors, AuthorityGrantValidationErrorCode.InvalidHash, "$.contentHash", "Grant content hash must match the complete canonical immutable snapshot.");
        }

        return Result(errors);
    }

    /// <summary>Validates one append-only grant-operation evidence record.</summary>
    public static AuthorityGrantValidationResult Validate(AuthorityGrantOperationEvidence? evidence)
    {
        var errors = new List<AuthorityGrantValidationError>();
        if (evidence is null)
        {
            Add(errors, AuthorityGrantValidationErrorCode.Required, "$", "Grant operation evidence is required.");
            return Result(errors);
        }

        ValidateSchema(evidence.SchemaVersion, errors);
        ValidateToken(evidence.OperationId, "$.operationId", AuthorityGrantContractLimits.MaxOperationIdCharacters, errors);
        ValidateSha256(evidence.RequestHash, "$.requestHash", errors);
        if (!Enum.IsDefined(evidence.Kind) || evidence.Kind == AuthorityGrantOperationKind.Unknown)
        {
            Add(errors, AuthorityGrantValidationErrorCode.InvalidLifecycle, "$.kind", "A supported operation kind is required.");
        }

        if (!Enum.IsDefined(evidence.Outcome) || evidence.Outcome == AuthorityGrantOperationOutcome.Unknown)
        {
            Add(errors, AuthorityGrantValidationErrorCode.InvalidLifecycle, "$.outcome", "A supported operation outcome is required.");
        }

        if (!IsSupportedOutcomeFailure(evidence.Outcome, evidence.FailureCode))
        {
            Add(errors, AuthorityGrantValidationErrorCode.InvalidLifecycle, "$.failureCode", "Operation outcome and failure classification are inconsistent.");
        }

        if (evidence.GrantId is null || evidence.ActorId is null || evidence.Reason is null)
        {
            Add(errors, AuthorityGrantValidationErrorCode.InvalidIdentity, "$.binding", "Grant, actor, and reason bindings are required.");
        }

        if (evidence.ExpectedRevision is < 0 or > int.MaxValue)
        {
            Add(errors, AuthorityGrantValidationErrorCode.InvalidLineage, "$.expectedRevision", "Expected revision must fit the bounded grant-revision domain.");
        }

        ValidateSha256(evidence.AuthorityEvidenceHash, "$.authorityEvidenceHash", errors);
        if (evidence.DependencyEvidenceHash is not null)
        {
            ValidateSha256(evidence.DependencyEvidenceHash, "$.dependencyEvidenceHash", errors);
        }

        var requiresDependencyEvidence = RequiresDependencyEvidence(evidence);
        if (requiresDependencyEvidence != (evidence.DependencyEvidenceHash is not null))
        {
            Add(errors, AuthorityGrantValidationErrorCode.InvalidLifecycle, "$.dependencyEvidenceHash", "Dependency evidence must be present only for committed authority-producing operations and ceiling-exceeded dispositions.");
        }

        ValidateUtc(evidence.RecordedAtUtc, "$.recordedAtUtc", errors);
        if (evidence.Outcome == AuthorityGrantOperationOutcome.Committed && evidence.ResultingGrant is null
            || evidence.Outcome != AuthorityGrantOperationOutcome.Committed && evidence.ResultingGrant is not null)
        {
            Add(errors, AuthorityGrantValidationErrorCode.InvalidLifecycle, "$.resultingGrant", "Only a newly committed operation may name a successor grant revision.");
        }

        if (evidence.ResultingGrant is { } reference
            && (reference.GrantId is null
                || reference.Revision is null
                || !AuthorityGrantHash.IsCanonical(reference.ContentHash)
                || evidence.GrantId is not null && !evidence.GrantId.Equals(reference.GrantId)
                || evidence.ExpectedRevision >= int.MaxValue
                || reference.Revision.Value != evidence.ExpectedRevision + 1))
        {
            Add(errors, AuthorityGrantValidationErrorCode.InvalidLineage, "$.resultingGrant", "The successor reference must identify this exact grant, the contiguous expected revision, and a canonical hash.");
        }

        if ((evidence.Kind == AuthorityGrantOperationKind.Create && evidence.ExpectedRevision != 0)
            || (evidence.Kind != AuthorityGrantOperationKind.Create && evidence.Kind != AuthorityGrantOperationKind.Unknown && evidence.ExpectedRevision == 0))
        {
            Add(errors, AuthorityGrantValidationErrorCode.InvalidLineage, "$.expectedRevision", "Create expects revision zero and every successor operation expects an existing positive revision.");
        }

        return Result(errors);
    }

    /// <summary>Validates one legal contiguous immutable lifecycle successor.</summary>
    public static AuthorityGrantValidationResult ValidateTransition(AuthorityGrant? current, AuthorityGrant? next, AuthorityGrantOperationKind kind)
    {
        var errors = new List<AuthorityGrantValidationError>();
        AddNested(errors, Validate(current), "$.current");
        AddNested(errors, Validate(next), "$.next");
        if (errors.Count > 0)
        {
            return Result(errors);
        }

        if (kind is AuthorityGrantOperationKind.Unknown or AuthorityGrantOperationKind.Create || !Enum.IsDefined(kind))
        {
            Add(errors, AuthorityGrantValidationErrorCode.InvalidLifecycle, "$.kind", "A successor transition requires Narrow, Suspend, Replace, Revoke, or Expire.");
            return Result(errors);
        }

        if (!current!.GrantId.Equals(next!.GrantId)
            || next.Revision.Value != current.Revision.Value + 1
            || next.PredecessorRevision is null
            || !next.PredecessorRevision.Equals(current.Revision)
            || !string.Equals(next.PredecessorContentHash, current.ContentHash, StringComparison.Ordinal))
        {
            Add(errors, AuthorityGrantValidationErrorCode.InvalidLineage, "$.next", "A successor must extend the exact current immutable revision and hash.");
        }

        if (next.RecordedAtUtc < current.RecordedAtUtc)
        {
            Add(errors, AuthorityGrantValidationErrorCode.InvalidBoundary, "$.next.recordedAtUtc", "Successor evidence cannot precede current evidence.");
        }

        if (current.Status is AuthorityGrantLifecycleStatus.Revoked or AuthorityGrantLifecycleStatus.Expired)
        {
            Add(errors, AuthorityGrantValidationErrorCode.InvalidLifecycle, "$.current.status", "Revoked and expired grants are terminal.");
            return Result(errors);
        }

        switch (kind)
        {
            case AuthorityGrantOperationKind.Narrow:
                if (next.Status != current.Status
                    || !Equals(next.Binding, current.Binding)
                    || !AuthorityCeilingSubset.IsStrictSubset(next.RequestedCeiling, current.RequestedCeiling)
                    || WidensBoundary(current.Boundary, next.Boundary))
                {
                    Add(errors, AuthorityGrantValidationErrorCode.AuthorityWidening, "$.next", "Narrow must retain exact pins and status while strictly reducing authority and never widening boundaries.");
                }

                break;
            case AuthorityGrantOperationKind.Suspend:
                ValidatePostureOnly(current, next, AuthorityGrantLifecycleStatus.Active, AuthorityGrantLifecycleStatus.Suspended, errors);
                break;
            case AuthorityGrantOperationKind.Replace:
                if (current.Status is not (AuthorityGrantLifecycleStatus.Active or AuthorityGrantLifecycleStatus.Suspended) || next.Status != AuthorityGrantLifecycleStatus.Active)
                {
                    Add(errors, AuthorityGrantValidationErrorCode.InvalidLifecycle, "$.next.status", "Replace is the only freshly authorized transition to an active successor.");
                }

                break;
            case AuthorityGrantOperationKind.Revoke:
                ValidateTerminal(current, next, AuthorityGrantLifecycleStatus.Revoked, errors);
                break;
            case AuthorityGrantOperationKind.Expire:
                ValidateTerminal(current, next, AuthorityGrantLifecycleStatus.Expired, errors);
                if (current.Boundary.ExpiresAtUtc is not { } expiry || expiry > next.RecordedAtUtc)
                {
                    Add(errors, AuthorityGrantValidationErrorCode.InvalidBoundary, "$.next.recordedAtUtc", "Expire requires a declared expiry endpoint at or before trusted operation evidence time.");
                }

                break;
        }

        return Result(errors);
    }

    internal static AuthorityGrantValidationResult ValidateForHash(AuthorityGrant? grant) => Result(ValidateStructure(grant));

    private static List<AuthorityGrantValidationError> ValidateStructure(AuthorityGrant? grant)
    {
        var errors = new List<AuthorityGrantValidationError>();
        if (grant is null)
        {
            Add(errors, AuthorityGrantValidationErrorCode.Required, "$", "Authority grant is required.");
            return errors;
        }

        ValidateSchema(grant.SchemaVersion, errors);
        if (grant.GrantId is null || grant.Revision is null || grant.ChangedByActorId is null || grant.Reason is null)
        {
            Add(errors, AuthorityGrantValidationErrorCode.InvalidIdentity, "$.identity", "Grant revision, actor, and reason bindings are required.");
        }

        if (grant.Revision is not null)
        {
            if (grant.Revision.Value == 1 && (grant.PredecessorRevision is not null || grant.PredecessorContentHash is not null)
                || grant.Revision.Value > 1 && (grant.PredecessorRevision is null || grant.PredecessorRevision.Value != grant.Revision.Value - 1 || !AuthorityGrantHash.IsCanonical(grant.PredecessorContentHash)))
            {
                Add(errors, AuthorityGrantValidationErrorCode.InvalidLineage, "$.predecessorRevision", "Revision 1 has no predecessor and every successor cites the exact contiguous predecessor hash.");
            }
        }

        if (!Enum.IsDefined(grant.Status) || grant.Status == AuthorityGrantLifecycleStatus.Unknown)
        {
            Add(errors, AuthorityGrantValidationErrorCode.InvalidLifecycle, "$.status", "A supported lifecycle status is required.");
        }

        ValidateBinding(grant.Binding, errors);
        if (!AuthorityProfileValidator.ValidateCeiling(grant.RequestedCeiling).IsValid)
        {
            Add(errors, AuthorityGrantValidationErrorCode.InvalidCeiling, "$.requestedCeiling", "Requested authority ceiling must satisfy the bounded authority contract.");
        }

        ValidateBoundary(grant.Boundary, errors);
        ValidateUtc(grant.RecordedAtUtc, "$.recordedAtUtc", errors);
        return errors;
    }

    private static void ValidateBinding(AuthorityGrantBinding? binding, List<AuthorityGrantValidationError> errors)
    {
        if (binding?.Profile?.Reference?.ProfileId is null
            || binding.Profile.Reference.Revision is null
            || binding.Profile.ContentHash is null
            || binding.Role?.Identity is null
            || !ContextualRoleId.IsValid(binding.Role.Identity.RoleId)
            || binding.Role.Identity.Revision < 1
            || !IsLowerSha256(binding.Role.ContentHash)
            || !GovernedLoopRevisionContractValidator.Validate(binding.Loop).IsValid)
        {
            Add(errors, AuthorityGrantValidationErrorCode.InvalidIdentity, "$.binding", "Exact profile, role, and loop publication pins are required and must be canonical.");
        }
    }

    private static void ValidateBoundary(AuthorityGrantBoundary? boundary, List<AuthorityGrantValidationError> errors)
    {
        if (boundary is null
            || boundary.EffectiveAtUtc == default
            || boundary.EffectiveAtUtc.Offset != TimeSpan.Zero
            || boundary.ExpiresAtUtc is { } expiry && (expiry.Offset != TimeSpan.Zero || expiry <= boundary.EffectiveAtUtc)
            || !Enum.IsDefined(boundary.CompletionConstraint)
            || boundary.CompletionConstraint == AuthorityGrantCompletionConstraintKind.Unknown)
        {
            Add(errors, AuthorityGrantValidationErrorCode.InvalidBoundary, "$.boundary", "Effective, expiry, and completion boundaries must be canonical and internally ordered.");
        }
    }

    private static void ValidatePostureOnly(AuthorityGrant current, AuthorityGrant next, AuthorityGrantLifecycleStatus expectedCurrent, AuthorityGrantLifecycleStatus expectedNext, List<AuthorityGrantValidationError> errors)
    {
        if (current.Status != expectedCurrent || next.Status != expectedNext || !SameDeclaration(current, next))
        {
            Add(errors, AuthorityGrantValidationErrorCode.InvalidLifecycle, "$.next", "This lifecycle transition may change posture only and must preserve the exact declaration.");
        }
    }

    private static void ValidateTerminal(AuthorityGrant current, AuthorityGrant next, AuthorityGrantLifecycleStatus expectedNext, List<AuthorityGrantValidationError> errors)
    {
        if (current.Status is not (AuthorityGrantLifecycleStatus.Active or AuthorityGrantLifecycleStatus.Suspended) || next.Status != expectedNext || !SameDeclaration(current, next))
        {
            Add(errors, AuthorityGrantValidationErrorCode.InvalidLifecycle, "$.next", "A terminal successor must preserve the exact declaration and start from active or suspended posture.");
        }
    }

    private static bool SameDeclaration(AuthorityGrant current, AuthorityGrant next)
        => Equals(current.Binding, next.Binding)
            && AuthorityCeilingSubset.IsEqual(current.RequestedCeiling, next.RequestedCeiling)
            && Equals(current.Boundary, next.Boundary);

    private static bool WidensBoundary(AuthorityGrantBoundary current, AuthorityGrantBoundary next)
    {
        return next.EffectiveAtUtc < current.EffectiveAtUtc
            || current.ExpiresAtUtc is not null && (next.ExpiresAtUtc is null || next.ExpiresAtUtc > current.ExpiresAtUtc)
            || current.CompletionConstraint == AuthorityGrantCompletionConstraintKind.FirstBoundRunCompletion && next.CompletionConstraint != current.CompletionConstraint;
    }

    private static void ValidateSchema(int value, List<AuthorityGrantValidationError> errors)
    {
        if (value != AuthorityGrantContractLimits.CurrentSchemaVersion)
        {
            Add(errors, AuthorityGrantValidationErrorCode.UnsupportedSchemaVersion, "$.schemaVersion", "Schema version must be 1.");
        }
    }

    private static void ValidateToken(string? value, string path, int maximumLength, List<AuthorityGrantValidationError> errors)
    {
        if (!AuthorityTextRules.IsToken(value, maximumLength))
        {
            Add(errors, AuthorityGrantValidationErrorCode.InvalidIdentity, path, "Identifier must be a bounded canonical authority token.");
        }
    }

    private static void ValidateSha256(string? value, string path, List<AuthorityGrantValidationError> errors)
    {
        if (!IsLowerSha256(value))
        {
            Add(errors, AuthorityGrantValidationErrorCode.InvalidHash, path, "Evidence hash must be canonical lowercase SHA-256 hexadecimal.");
        }
    }

    private static bool IsLowerSha256(string? value)
        => value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsSupportedOutcomeFailure(AuthorityGrantOperationOutcome outcome, AuthorityGrantOperationFailureCode failureCode)
    {
        if (!Enum.IsDefined(outcome) || outcome == AuthorityGrantOperationOutcome.Unknown || !Enum.IsDefined(failureCode))
        {
            return false;
        }

        return outcome switch
        {
            AuthorityGrantOperationOutcome.Committed => failureCode == AuthorityGrantOperationFailureCode.None,
            AuthorityGrantOperationOutcome.Invalid => failureCode == AuthorityGrantOperationFailureCode.InvalidRequest,
            AuthorityGrantOperationOutcome.Denied => failureCode == AuthorityGrantOperationFailureCode.AuthorityDenied,
            AuthorityGrantOperationOutcome.NotFound => failureCode is AuthorityGrantOperationFailureCode.LifecycleConflict
                or AuthorityGrantOperationFailureCode.ProfileUnavailable
                or AuthorityGrantOperationFailureCode.RoleUnavailable
                or AuthorityGrantOperationFailureCode.LoopUnavailable,
            AuthorityGrantOperationOutcome.Conflict => failureCode is AuthorityGrantOperationFailureCode.LifecycleConflict
                or AuthorityGrantOperationFailureCode.OperationConflict
                or AuthorityGrantOperationFailureCode.CeilingExceeded
                or AuthorityGrantOperationFailureCode.BoundaryConflict,
            AuthorityGrantOperationOutcome.LimitExceeded => failureCode == AuthorityGrantOperationFailureCode.LimitExceeded,
            AuthorityGrantOperationOutcome.Unavailable => failureCode is AuthorityGrantOperationFailureCode.AuthorityUnavailable
                or AuthorityGrantOperationFailureCode.ProfileUnavailable
                or AuthorityGrantOperationFailureCode.RoleUnavailable
                or AuthorityGrantOperationFailureCode.LoopUnavailable
                or AuthorityGrantOperationFailureCode.StoreUnavailable,
            AuthorityGrantOperationOutcome.Ambiguous => failureCode == AuthorityGrantOperationFailureCode.StoreAmbiguous,
            _ => false,
        };
    }

    private static bool RequiresDependencyEvidence(AuthorityGrantOperationEvidence evidence)
        => evidence.Outcome == AuthorityGrantOperationOutcome.Committed
            && evidence.Kind is AuthorityGrantOperationKind.Create or AuthorityGrantOperationKind.Narrow or AuthorityGrantOperationKind.Replace
            || evidence.Outcome == AuthorityGrantOperationOutcome.Conflict
            && evidence.FailureCode == AuthorityGrantOperationFailureCode.CeilingExceeded;

    private static void ValidateUtc(DateTimeOffset value, string path, List<AuthorityGrantValidationError> errors)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            Add(errors, AuthorityGrantValidationErrorCode.InvalidBoundary, path, "Timestamp must be a non-default exact UTC value.");
        }
    }

    private static void AddNested(List<AuthorityGrantValidationError> errors, AuthorityGrantValidationResult result, string prefix)
    {
        foreach (var error in result.Errors)
        {
            Add(errors, error.Code, prefix + (error.Path == "$" ? string.Empty : error.Path[1..]), error.Message);
        }
    }

    private static void Add(List<AuthorityGrantValidationError> errors, AuthorityGrantValidationErrorCode code, string path, string message)
    {
        if (errors.Count < AuthorityGrantContractLimits.MaxValidationErrors)
        {
            errors.Add(new AuthorityGrantValidationError(code, path, message));
        }
    }

    private static AuthorityGrantValidationResult Result(IReadOnlyList<AuthorityGrantValidationError> errors)
    {
        var snapshot = Array.AsReadOnly(errors.Take(AuthorityGrantContractLimits.MaxValidationErrors).ToArray());
        return new AuthorityGrantValidationResult(snapshot, snapshot.Count == 0);
    }
}
