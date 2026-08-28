using System.Globalization;
using EmbodySense.Core.Application.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.HumanInput;
using EmbodySense.Core.Common.HumanInput.Lifecycle;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Models;

namespace EmbodySense.Core.Application.HumanInput.Lifecycle;

/// <summary>Validates bounded Human Input lifecycle command shape without consulting authority, trusted time, grants, or persistence.</summary>
public static class HumanInputRequestLifecycleCommandValidator
{
    /// <summary>Returns every bounded deterministic command-shape violation.</summary>
    /// <param name="command">The potentially malformed command.</param>
    /// <returns>A defensive bounded list of value-free validation errors.</returns>
    public static IReadOnlyList<HumanInputRequestLifecycleMutationValidationError> Validate(HumanInputRequestLifecycleCommand? command)
    {
        var errors = new List<HumanInputRequestLifecycleMutationValidationError>();
        if (command is null)
        {
            Add(errors, HumanInputRequestLifecycleMutationValidationErrorCode.CommandRequired, "$", "A Human Input lifecycle command is required.");
            return Result(errors);
        }

        if (command.SchemaVersion != HumanInputRequestLifecycleCommand.CurrentSchemaVersion)
        {
            Add(errors, HumanInputRequestLifecycleMutationValidationErrorCode.UnsupportedSchemaVersion, "$.schemaVersion", "Human Input lifecycle command schema version must be 1.");
        }

        if (!HumanInputIdentifier.IsValid(command.OperationId, HumanInputRequestLifecycleContractLimits.MaxOperationIdCharacters))
        {
            Add(errors, HumanInputRequestLifecycleMutationValidationErrorCode.InvalidIdentifier, "$.operationId", "A bounded canonical operation identifier is required.");
        }

        if (!HumanInputIdentifier.IsValid(command.RequestId))
        {
            Add(errors, HumanInputRequestLifecycleMutationValidationErrorCode.InvalidIdentifier, "$.requestId", "A bounded canonical request identifier is required.");
        }

        var kindIsValid = Enum.IsDefined(command.Kind) && command.Kind != HumanInputRequestLifecycleOperationKind.Unknown;
        if (!kindIsValid)
        {
            Add(errors, HumanInputRequestLifecycleMutationValidationErrorCode.InvalidOperationKind, "$.kind", "A supported lifecycle operation is required.");
        }

        ValidateExpectedState(command, kindIsValid, errors);
        ValidateCandidate(command, kindIsValid, errors);
        ValidateGrant(command, kindIsValid, errors);

        if (command.Reason is null || !AuthorityPurpose.TryParse(command.Reason.Value, out var reason, out _) || !Equals(reason, command.Reason))
        {
            Add(errors, HumanInputRequestLifecycleMutationValidationErrorCode.InvalidReason, "$.reason", "A bounded canonical non-secret lifecycle reason is required.");
        }

        if (!HumanInputRequestLifecycleCommandHash.Matches(command))
        {
            Add(errors, HumanInputRequestLifecycleMutationValidationErrorCode.InvalidRequestHash, "$.requestHash", "The command hash must match the complete canonical lifecycle intent.");
        }

        return Result(errors);
    }

    private static void ValidateExpectedState(
        HumanInputRequestLifecycleCommand command,
        bool kindIsValid,
        List<HumanInputRequestLifecycleMutationValidationError> errors)
    {
        if (!kindIsValid)
        {
            return;
        }

        if (command.Kind == HumanInputRequestLifecycleOperationKind.Create)
        {
            if (command.ExpectedLifecycleVersion != 0
                || command.ExpectedLifecycleStatus != HumanInputRequestLifecycleStatus.Unknown
                || command.ExpectedRequest is not null
                || command.ExpectedBinding is not null)
            {
                Add(errors, HumanInputRequestLifecycleMutationValidationErrorCode.InvalidExpectedState, "$.expectedLifecycleVersion", "Create requires an absent expected lifecycle.");
            }

            return;
        }

        if (command.ExpectedLifecycleVersion is < 1 or > HumanInputRequestLifecycleContractLimits.MaxLifecycleVersion
            || command.ExpectedLifecycleStatus != HumanInputRequestLifecycleStatus.Pending
            || command.ExpectedRequest is null
            || !HumanInputRequestLifecycleValidator.ValidateReference(command.ExpectedRequest).IsValid
            || !string.Equals(command.ExpectedRequest.RequestId, command.RequestId, StringComparison.Ordinal)
            || command.ExpectedBinding is null
            || !BindingIsValid(command.ExpectedBinding))
        {
            Add(errors, HumanInputRequestLifecycleMutationValidationErrorCode.InvalidExpectedState, "$.expectedRequest", "A non-create operation requires the exact pending lifecycle version, current request reference, and request binding.");
        }
    }

    private static void ValidateCandidate(
        HumanInputRequestLifecycleCommand command,
        bool kindIsValid,
        List<HumanInputRequestLifecycleMutationValidationError> errors)
    {
        if (!kindIsValid)
        {
            return;
        }

        var requiresCandidate = RequiresCandidate(command.Kind);
        if (requiresCandidate != (command.CandidateRequest is not null))
        {
            Add(errors, HumanInputRequestLifecycleMutationValidationErrorCode.InvalidCandidateRequest, "$.candidateRequest", "Candidate presence does not match the requested lifecycle operation.");
            return;
        }

        if (!requiresCandidate)
        {
            return;
        }

        if (!HumanInputRequestSnapshot.TryCapture(command.CandidateRequest, out var candidate, out _) || candidate is null)
        {
            Add(errors, HumanInputRequestLifecycleMutationValidationErrorCode.InvalidCandidateRequest, "$.candidateRequest", "The candidate must be one bounded valid immutable Human Input request.");
            return;
        }

        var requestIdentityMatches = command.Kind == HumanInputRequestLifecycleOperationKind.Supersede
            ? !string.Equals(candidate.RequestId, command.RequestId, StringComparison.Ordinal)
            : string.Equals(candidate.RequestId, command.RequestId, StringComparison.Ordinal);
        if (!requestIdentityMatches)
        {
            Add(errors, HumanInputRequestLifecycleMutationValidationErrorCode.InvalidOperationShape, "$.candidateRequest.requestId", "Candidate request identity does not match the requested operation.");
        }

        if (command.Kind != HumanInputRequestLifecycleOperationKind.Create
            && !Equals(candidate.Binding, command.ExpectedBinding))
        {
            Add(errors, HumanInputRequestLifecycleMutationValidationErrorCode.InvalidOperationShape, "$.candidateRequest.binding", "A non-create candidate must retain the exact expected request binding.");
        }

        if (command.Kind is HumanInputRequestLifecycleOperationKind.Reroute or HumanInputRequestLifecycleOperationKind.Amend
            && command.ExpectedRequest is { } expected
            && (string.Equals(candidate.RequestVersionId, expected.RequestVersionId, StringComparison.Ordinal)
                || string.Equals(candidate.RequestHash, expected.RequestHash, StringComparison.Ordinal)))
        {
            Add(errors, HumanInputRequestLifecycleMutationValidationErrorCode.InvalidOperationShape, "$.candidateRequest.requestVersionId", "A replacement candidate must identify a distinct immutable request version.");
        }
    }

    private static void ValidateGrant(
        HumanInputRequestLifecycleCommand command,
        bool kindIsValid,
        List<HumanInputRequestLifecycleMutationValidationError> errors)
    {
        if (!kindIsValid)
        {
            return;
        }

        var requiresGrant = RequiresGrant(command.Kind);
        if (requiresGrant != (command.GrantReference is not null))
        {
            Add(errors, HumanInputRequestLifecycleMutationValidationErrorCode.InvalidGrantReference, "$.grantReference", "Delivery-producing operations require one exact grant; cleanup operations prohibit grants.");
            return;
        }

        if (command.GrantReference is not { } reference)
        {
            return;
        }

        if (reference.GrantId is null
            || reference.Revision is null
            || !AuthorityGrantId.TryParse(reference.GrantId.Value, out _, out _)
            || !AuthorityGrantRevision.TryParse(reference.Revision.Value.ToString(CultureInfo.InvariantCulture), out _, out _)
            || !IsGrantHash(reference.ContentHash))
        {
            Add(errors, HumanInputRequestLifecycleMutationValidationErrorCode.InvalidGrantReference, "$.grantReference", "The grant reference must identify one exact canonical immutable grant revision.");
        }
    }

    private static bool RequiresCandidate(HumanInputRequestLifecycleOperationKind kind)
        => kind is HumanInputRequestLifecycleOperationKind.Create
            or HumanInputRequestLifecycleOperationKind.Reroute
            or HumanInputRequestLifecycleOperationKind.Amend
            or HumanInputRequestLifecycleOperationKind.Supersede;

    private static bool RequiresGrant(HumanInputRequestLifecycleOperationKind kind)
        => kind is HumanInputRequestLifecycleOperationKind.Create
            or HumanInputRequestLifecycleOperationKind.Remind
            or HumanInputRequestLifecycleOperationKind.Reroute
            or HumanInputRequestLifecycleOperationKind.Amend
            or HumanInputRequestLifecycleOperationKind.Supersede;

    private static bool BindingIsValid(HumanInputRequestBinding binding)
        => ContextualRoleWorkspaceId.IsValid(binding.WorkspaceId)
            && HumanInputIdentifier.IsValid(binding.LoopGraphId)
            && HumanInputIdentifier.IsValid(binding.LoopRevisionId)
            && HumanInputIdentifier.IsValid(binding.NodeId)
            && HumanInputIdentifier.IsValid(binding.RunId)
            && HumanInputIdentifier.IsValid(binding.CheckpointId);

    private static bool IsGrantHash(string? value)
        => value is { Length: 7 + HumanInputRequestLifecycleContractLimits.Sha256HexCharacters }
            && value.StartsWith("sha256:", StringComparison.Ordinal)
            && value[7..].All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static IReadOnlyList<HumanInputRequestLifecycleMutationValidationError> Result(
        IEnumerable<HumanInputRequestLifecycleMutationValidationError> errors)
        => Array.AsReadOnly(errors.Distinct().Take(HumanInputRequestLifecycleContractLimits.MaxValidationErrors).ToArray());

    private static void Add(
        List<HumanInputRequestLifecycleMutationValidationError> errors,
        HumanInputRequestLifecycleMutationValidationErrorCode code,
        string path,
        string message)
    {
        if (errors.Count >= HumanInputRequestLifecycleContractLimits.MaxValidationErrors)
        {
            return;
        }

        errors.Add(new HumanInputRequestLifecycleMutationValidationError(code, path, message));
    }
}
