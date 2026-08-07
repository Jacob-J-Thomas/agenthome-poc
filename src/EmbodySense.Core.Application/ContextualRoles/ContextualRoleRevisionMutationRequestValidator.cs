using EmbodySense.Core.Application.ContextualRoles.Models;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.ContextualRoles.Models;

namespace EmbodySense.Core.Application.ContextualRoles;

/// <summary>Validates the idempotency, lifecycle, and optimistic-concurrency shape of contextual-role mutations.</summary>
public static class ContextualRoleRevisionMutationRequestValidator
{
    /// <summary>Validates a request without reading or mutating persistence.</summary>
    /// <param name="request">The candidate mutation request.</param>
    /// <returns>Every deterministic contract error.</returns>
    public static IReadOnlyList<ContextualRoleValidationError> Validate(ContextualRoleRevisionMutationRequest? request)
    {
        var errors = new List<ContextualRoleValidationError>();
        if (request is null)
        {
            Add(errors, "request_required", "$", "Contextual-role mutation request is required.");
            return errors;
        }

        if (!ContextualRoleId.IsValid(request.OperationId))
        {
            Add(errors, "invalid_operation_id", "operationId", "Operation id must be a bounded lowercase ASCII identifier.");
        }

        if (!ContextualRoleId.IsValid(request.RoleId))
        {
            Add(errors, "invalid_role_id", "roleId", "Role id must be a bounded lowercase ASCII identifier.");
        }

        if (!ContextualRoleId.IsValid(request.ActorId))
        {
            Add(errors, "invalid_actor_id", "actorId", "Actor id must be a bounded lowercase ASCII identifier.");
        }

        if (request.Kind is < ContextualRoleRevisionMutationKind.Create or > ContextualRoleRevisionMutationKind.Tombstone)
        {
            Add(errors, "invalid_mutation_kind", "kind", "Mutation kind must be create, replace, disable, reenable, or tombstone.");
        }

        if (request.RequestedAtUtc == default || request.RequestedAtUtc.Offset != TimeSpan.Zero)
        {
            Add(errors, "invalid_requested_at_utc", "requestedAtUtc", "Requested time must be a non-default UTC timestamp.");
        }

        ValidateRevisionRelationship(request, errors);
        if (!IsSha256(request.RequestHash))
        {
            Add(errors, "invalid_request_hash", "requestHash", "Request hash must be a 64-character lowercase SHA-256 digest.");
        }
        else if (CanComputeHash(request) && !ContextualRoleRevisionMutationRequestHash.Matches(request))
        {
            Add(errors, "request_hash_mismatch", "requestHash", "Request hash does not match the exact canonical mutation intent.");
        }

        return errors;
    }

    private static void ValidateRevisionRelationship(ContextualRoleRevisionMutationRequest request, List<ContextualRoleValidationError> errors)
    {
        if (request.Kind is ContextualRoleRevisionMutationKind.Create or ContextualRoleRevisionMutationKind.Replace)
        {
            var validation = ContextualRoleRevisionValidator.Validate(request.Revision);
            errors.AddRange(validation.Errors);
            if (request.Revision?.Identity is { } identity && !string.Equals(identity.RoleId, request.RoleId, StringComparison.Ordinal))
            {
                Add(errors, "role_identity_mismatch", "revision.identity.roleId", "Revision role id must match the mutation role id.");
            }

            if (request.Revision is { Status: not (ContextualRoleStatus.Draft or ContextualRoleStatus.Published) })
            {
                Add(errors, "invalid_revision_mutation_status", "revision.status", "Create and replace must append a draft or published revision; lifecycle transitions remain explicit operations.");
            }

            if (request.Kind == ContextualRoleRevisionMutationKind.Create)
            {
                if (request.ExpectedPreviousIdentity is not null)
                {
                    Add(errors, "unexpected_previous_identity", "expectedPreviousIdentity", "Create cannot name a previous revision.");
                }

                if (request.Revision?.Identity?.Revision != 1)
                {
                    Add(errors, "invalid_initial_revision", "revision.identity.revision", "Create must publish immutable revision 1.");
                }
            }
            else
            {
                ValidatePrevious(request, errors);
                if (request.ExpectedPreviousIdentity is { } previous && request.Revision?.Identity?.Revision != previous.Revision + 1)
                {
                    Add(errors, "nonsequential_replacement", "revision.identity.revision", "Replacement must append exactly one immutable revision.");
                }
            }
        }
        else
        {
            if (request.Revision is not null)
            {
                Add(errors, "unexpected_revision", "revision", "Lifecycle-only mutations cannot supply a replacement revision.");
            }

            ValidatePrevious(request, errors);
        }
    }

    private static void ValidatePrevious(ContextualRoleRevisionMutationRequest request, List<ContextualRoleValidationError> errors)
    {
        if (request.ExpectedPreviousIdentity is null)
        {
            Add(errors, "previous_identity_required", "expectedPreviousIdentity", "This mutation requires the exact current revision identity.");
            return;
        }

        if (!ContextualRoleId.IsValid(request.ExpectedPreviousIdentity.RoleId) || request.ExpectedPreviousIdentity.Revision < 1)
        {
            Add(errors, "invalid_previous_identity", "expectedPreviousIdentity", "Previous identity must contain a valid role id and positive revision.");
        }
        else if (!string.Equals(request.ExpectedPreviousIdentity.RoleId, request.RoleId, StringComparison.Ordinal))
        {
            Add(errors, "previous_role_mismatch", "expectedPreviousIdentity.roleId", "Previous identity role id must match the mutation role id.");
        }
    }

    private static bool CanComputeHash(ContextualRoleRevisionMutationRequest request)
    {
        try
        {
            _ = ContextualRoleRevisionMutationRequestHash.Compute(request);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool IsSha256(string? value) => value is { Length: ContextualRoleLimits.Sha256HexCharacters }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void Add(List<ContextualRoleValidationError> errors, string code, string field, string message) => errors.Add(new ContextualRoleValidationError(code, field, message));
}
