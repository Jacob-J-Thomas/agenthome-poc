using EmbodySense.Core.Application.Governance.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Grants.Models;

namespace EmbodySense.Core.Application.Governance.Authority.Grants;

/// <summary>Validates bounded grant mutation intent without consulting authority, dependencies, time, or persistence.</summary>
public static class AuthorityGrantMutationRequestValidator
{
    /// <summary>Returns every bounded deterministic request-shape error.</summary>
    public static IReadOnlyList<AuthorityGrantMutationValidationError> Validate(AuthorityGrantMutationRequest? request)
    {
        var errors = new List<AuthorityGrantMutationValidationError>();
        if (request is null)
        {
            Add(errors, AuthorityGrantMutationValidationErrorCode.RequestRequired, "$");
            return Result(errors);
        }

        if (request.SchemaVersion != AuthorityGrantContractLimits.CurrentSchemaVersion)
        {
            Add(errors, AuthorityGrantMutationValidationErrorCode.UnsupportedSchemaVersion, "schemaVersion");
        }

        if (!IsOperationToken(request.OperationId))
        {
            Add(errors, AuthorityGrantMutationValidationErrorCode.InvalidOperationId, "operationId");
        }

        if (request.GrantId is null || !AuthorityGrantId.TryParse(request.GrantId.Value, out _, out _))
        {
            Add(errors, AuthorityGrantMutationValidationErrorCode.InvalidGrantId, "grantId");
        }

        if (!Enum.IsDefined(request.Kind) || request.Kind == AuthorityGrantOperationKind.Unknown)
        {
            Add(errors, AuthorityGrantMutationValidationErrorCode.InvalidOperationKind, "kind");
        }

        ValidateExpectation(request, errors);
        ValidateCandidate(request, errors);
        if (request.ActorId is null || !AuthorityActorId.TryParse(request.ActorId.Value, out _, out _))
        {
            Add(errors, AuthorityGrantMutationValidationErrorCode.InvalidActor, "actorId");
        }

        if (request.Reason is null || !AuthorityPurpose.TryParse(request.Reason.Value, out _, out _))
        {
            Add(errors, AuthorityGrantMutationValidationErrorCode.InvalidReason, "reason");
        }

        if (!AuthorityGrantMutationRequestHash.Matches(request))
        {
            Add(errors, AuthorityGrantMutationValidationErrorCode.RequestHashMismatch, "requestHash");
        }

        return Result(errors);
    }

    private static void ValidateExpectation(AuthorityGrantMutationRequest request, List<AuthorityGrantMutationValidationError> errors)
    {
        if (request.ExpectedRevision is < 0 or > int.MaxValue)
        {
            Add(errors, AuthorityGrantMutationValidationErrorCode.InvalidExpectedRevision, "expectedRevision");
            return;
        }

        if (request.Kind == AuthorityGrantOperationKind.Create)
        {
            if (request.ExpectedRevision != 0)
            {
                Add(errors, AuthorityGrantMutationValidationErrorCode.InvalidExpectedRevision, "expectedRevision");
            }

            if (request.ExpectedStatus != AuthorityGrantLifecycleStatus.Unknown)
            {
                Add(errors, AuthorityGrantMutationValidationErrorCode.InvalidExpectedStatus, "expectedStatus");
            }

            return;
        }

        if (request.ExpectedRevision == 0)
        {
            Add(errors, AuthorityGrantMutationValidationErrorCode.InvalidExpectedRevision, "expectedRevision");
        }

        var allowed = request.Kind switch
        {
            AuthorityGrantOperationKind.Narrow => request.ExpectedStatus is AuthorityGrantLifecycleStatus.Active or AuthorityGrantLifecycleStatus.Suspended,
            AuthorityGrantOperationKind.Suspend => request.ExpectedStatus == AuthorityGrantLifecycleStatus.Active,
            AuthorityGrantOperationKind.Replace => request.ExpectedStatus is AuthorityGrantLifecycleStatus.Active or AuthorityGrantLifecycleStatus.Suspended,
            AuthorityGrantOperationKind.Revoke => request.ExpectedStatus is AuthorityGrantLifecycleStatus.Active or AuthorityGrantLifecycleStatus.Suspended,
            AuthorityGrantOperationKind.Expire => request.ExpectedStatus is AuthorityGrantLifecycleStatus.Active or AuthorityGrantLifecycleStatus.Suspended,
            _ => false,
        };
        if (!allowed)
        {
            Add(errors, AuthorityGrantMutationValidationErrorCode.InvalidExpectedStatus, "expectedStatus");
        }
    }

    private static void ValidateCandidate(AuthorityGrantMutationRequest request, List<AuthorityGrantMutationValidationError> errors)
    {
        var requiresCandidate = request.Kind is AuthorityGrantOperationKind.Create or AuthorityGrantOperationKind.Narrow or AuthorityGrantOperationKind.Replace;
        if (!requiresCandidate)
        {
            if (request.CandidateBinding is not null || request.CandidateCeiling is not null || request.CandidateBoundary is not null)
            {
                Add(errors, AuthorityGrantMutationValidationErrorCode.InvalidCandidate, "candidate");
            }

            return;
        }

        if (request.CandidateBinding is null || request.CandidateCeiling is null || request.CandidateBoundary is null || request.GrantId is null || request.ActorId is null || request.Reason is null)
        {
            Add(errors, AuthorityGrantMutationValidationErrorCode.InvalidCandidate, "candidate");
            return;
        }

        if (!AuthorityGrantRevision.TryParse("1", out var revision, out _))
        {
            Add(errors, AuthorityGrantMutationValidationErrorCode.InvalidCandidate, "candidate");
            return;
        }

        try
        {
            var candidate = AuthorityGrantHash.Apply(new AuthorityGrant(
                AuthorityGrantContractLimits.CurrentSchemaVersion,
                request.GrantId,
                revision!,
                null,
                null,
                AuthorityGrantLifecycleStatus.Active,
                request.CandidateBinding,
                request.CandidateCeiling,
                request.CandidateBoundary,
                request.ActorId,
                request.Reason,
                request.CandidateBoundary.EffectiveAtUtc,
                string.Empty));
            if (!AuthorityGrantContractValidator.Validate(candidate).IsValid)
            {
                Add(errors, AuthorityGrantMutationValidationErrorCode.InvalidCandidate, "candidate");
            }
        }
        catch (ArgumentException)
        {
            Add(errors, AuthorityGrantMutationValidationErrorCode.InvalidCandidate, "candidate");
        }
    }

    private static void Add(List<AuthorityGrantMutationValidationError> errors, AuthorityGrantMutationValidationErrorCode code, string path)
    {
        if (errors.Count < AuthorityGrantContractLimits.MaxValidationErrors)
        {
            errors.Add(new AuthorityGrantMutationValidationError(code, path));
        }
    }

    private static IReadOnlyList<AuthorityGrantMutationValidationError> Result(IReadOnlyList<AuthorityGrantMutationValidationError> errors)
        => Array.AsReadOnly(errors.Distinct().ToArray());

    private static bool IsOperationToken(string? value)
        => value is { Length: > 0 and <= AuthorityGrantContractLimits.MaxOperationIdCharacters }
            && value[0] is >= 'a' and <= 'z' or >= '0' and <= '9'
            && value[^1] is >= 'a' and <= 'z' or >= '0' and <= '9'
            && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '.');
}
