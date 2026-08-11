using EmbodySense.Core.Application.HumanInput.Responses.Models;
using EmbodySense.Core.Common.HumanInput;
using EmbodySense.Core.Common.HumanInput.Lifecycle;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Responses;
using EmbodySense.Core.Common.HumanInput.Responses.Models;

namespace EmbodySense.Core.Application.HumanInput.Responses;

/// <summary>Validates bounded response-operation envelopes without authenticating, evaluating policy, or persisting response data.</summary>
public static class HumanInputResponseLifecycleCommandValidator
{
    /// <summary>Returns every deterministic bounded command-envelope error.</summary>
    /// <param name="command">The caller-owned command.</param>
    /// <returns>An immutable bounded error snapshot.</returns>
    public static IReadOnlyList<HumanInputResponseLifecycleMutationValidationError> Validate(HumanInputResponseLifecycleCommand? command)
    {
        var errors = new List<HumanInputResponseLifecycleMutationValidationError>();
        if (command is null)
        {
            Add(errors, HumanInputResponseLifecycleMutationValidationErrorCode.CommandRequired, "$", "A Human Input response command is required.");
            return Array.AsReadOnly(errors.ToArray());
        }

        if (command.SchemaVersion != HumanInputResponseLifecycleCommand.CurrentSchemaVersion)
        {
            Add(errors, HumanInputResponseLifecycleMutationValidationErrorCode.UnsupportedSchemaVersion, "schemaVersion", "Human Input response command schema version must be 1.");
        }
        ValidateIdentifier(command.OperationId, "operationId", errors);
        ValidateIdentifier(command.RequestId, "requestId", errors);
        if (!Enum.IsDefined(command.Kind) || command.Kind == HumanInputResponseOperationKind.Unknown)
        {
            Add(errors, HumanInputResponseLifecycleMutationValidationErrorCode.InvalidOperationKind, "kind", "A supported Submit, Withdraw, or Select operation is required.");
        }
        if (command.ExpectedLifecycleVersion is < 1 or > HumanInputRequestLifecycleContractLimits.MaxLifecycleVersion
            || command.ExpectedLifecycleStatus != HumanInputRequestLifecycleStatus.Pending)
        {
            Add(errors, HumanInputResponseLifecycleMutationValidationErrorCode.InvalidExpectedState, "expectedLifecycleVersion", "New response intent must target one exact pending request lifecycle version.");
        }
        if (!HumanInputRequestLifecycleValidator.ValidateReference(command.ExpectedRequest).IsValid
            || !string.Equals(command.RequestId, command.ExpectedRequest?.RequestId, StringComparison.Ordinal))
        {
            Add(errors, HumanInputResponseLifecycleMutationValidationErrorCode.InvalidRequestReference, "expectedRequest", "An exact request reference matching the target lifecycle is required.");
        }
        ValidateBinding(command.ExpectedBinding, errors);
        ValidateOperationShape(command, errors);
        if (!HumanInputResponseLifecycleCommandHash.Matches(command))
        {
            Add(errors, HumanInputResponseLifecycleMutationValidationErrorCode.InvalidCommandHash, "commandHash", "Command hash must exactly match every behavior-affecting response field.");
        }
        return Array.AsReadOnly(errors.ToArray());
    }

    private static void ValidateOperationShape(
        HumanInputResponseLifecycleCommand command,
        List<HumanInputResponseLifecycleMutationValidationError> errors)
    {
        if (command.TargetResponses.IsDefault || command.TargetResponses.Length > HumanInputResponseContractLimits.MaxSelectedResponses)
        {
            Add(errors, HumanInputResponseLifecycleMutationValidationErrorCode.InvalidOperationShape, "targetResponses", "Response targets must be an initialized bounded immutable array.");
            return;
        }
        foreach (var target in command.TargetResponses)
        {
            if (!HumanInputResponseContractValidator.ValidateReference(target).IsValid
                || !Equals(target.Request, command.ExpectedRequest))
            {
                Add(errors, HumanInputResponseLifecycleMutationValidationErrorCode.InvalidOperationShape, "targetResponses", "Every target must be one exact response reference for the expected request version.");
            }
        }

        var valueIsBounded = true;
        if (command.Value is not null)
        {
            try
            {
                _ = HumanInputResponseValueHash.Compute(command.Value);
            }
            catch (ArgumentException)
            {
                valueIsBounded = false;
                Add(errors, HumanInputResponseLifecycleMutationValidationErrorCode.UnboundedResponseValue, "value", "Response value must remain within canonical schema-1 command bounds.");
            }
        }

        if (command.Explanation is not null
            && !HumanInputText.IsValid(command.Explanation, HumanInputLimits.MaxExplanationCharacters, required: false))
        {
            Add(errors, HumanInputResponseLifecycleMutationValidationErrorCode.UnboundedResponseValue, "explanation", "Response explanation must be bounded canonical display-safe Unicode.");
        }

        switch (command.Kind)
        {
            case HumanInputResponseOperationKind.Submit:
                if (!HumanInputIdentifier.IsValid(command.ResponseId)
                    || command.Value is null
                    || !valueIsBounded
                    || command.TargetResponses.Length != 0)
                {
                    Add(errors, HumanInputResponseLifecycleMutationValidationErrorCode.InvalidOperationShape, "$", "Submit requires one bounded response identity and value and has no target response.");
                }
                break;
            case HumanInputResponseOperationKind.Withdraw:
            case HumanInputResponseOperationKind.Select:
                if (command.ResponseId is not null
                    || command.Value is not null
                    || command.Explanation is not null
                    || command.TargetResponses.Length != 1)
                {
                    Add(errors, HumanInputResponseLifecycleMutationValidationErrorCode.InvalidOperationShape, "$", "Withdraw and Select require exactly one target response and no submitted response fields.");
                }
                break;
        }
    }

    private static void ValidateBinding(
        EmbodySense.Core.Common.HumanInput.Models.HumanInputRequestBinding? binding,
        List<HumanInputResponseLifecycleMutationValidationError> errors)
    {
        if (binding is null
            || !HumanInputIdentifier.IsValid(binding.WorkspaceId)
            || !HumanInputIdentifier.IsValid(binding.LoopGraphId)
            || !HumanInputIdentifier.IsValid(binding.LoopRevisionId)
            || !HumanInputIdentifier.IsValid(binding.NodeId)
            || !HumanInputIdentifier.IsValid(binding.RunId)
            || !HumanInputIdentifier.IsValid(binding.CheckpointId))
        {
            Add(errors, HumanInputResponseLifecycleMutationValidationErrorCode.InvalidBinding, "expectedBinding", "An exact canonical workspace, graph, revision, node, run, and checkpoint binding is required.");
        }
    }

    private static void ValidateIdentifier(
        string? value,
        string path,
        List<HumanInputResponseLifecycleMutationValidationError> errors)
    {
        if (!HumanInputIdentifier.IsValid(value))
        {
            Add(errors, HumanInputResponseLifecycleMutationValidationErrorCode.InvalidIdentifier, path, "A bounded canonical lowercase Human Input identifier is required.");
        }
    }

    private static void Add(
        List<HumanInputResponseLifecycleMutationValidationError> errors,
        HumanInputResponseLifecycleMutationValidationErrorCode code,
        string path,
        string message)
    {
        if (errors.Count < HumanInputResponseContractLimits.MaxValidationErrors)
        {
            errors.Add(new HumanInputResponseLifecycleMutationValidationError(code, path, message));
        }
    }
}
