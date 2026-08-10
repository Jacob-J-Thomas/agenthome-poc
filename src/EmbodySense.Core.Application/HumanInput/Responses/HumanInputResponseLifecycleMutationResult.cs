using EmbodySense.Core.Application.HumanInput.Responses.Models;

namespace EmbodySense.Core.Application.HumanInput.Responses.Models;

public sealed partial record HumanInputResponseLifecycleMutationResult
{
    /// <summary>Creates a privacy-safe immutable response-operation result.</summary>
    /// <param name="status">The closed operation status.</param>
    /// <param name="operationId">The operation identity when safely captured.</param>
    /// <param name="commandHash">The canonical exact-intent hash when safely established.</param>
    /// <param name="operation">Value-free durable operation proof when available.</param>
    /// <param name="projection">Privacy-safe current response posture when scope can be proved.</param>
    /// <param name="validationErrors">The deterministic command-envelope errors.</param>
    public HumanInputResponseLifecycleMutationResult(
        HumanInputResponseLifecycleMutationStatus status,
        string operationId,
        string commandHash,
        HumanInputResponseLifecycleOperationProof? operation,
        HumanInputResponseLifecycleProjection? projection,
        IReadOnlyList<HumanInputResponseLifecycleMutationValidationError> validationErrors)
    {
        Status = status;
        OperationId = operationId;
        CommandHash = commandHash;
        Operation = operation;
        Projection = projection;
        ValidationErrors = Array.AsReadOnly(validationErrors?.ToArray() ?? throw new ArgumentNullException(nameof(validationErrors)));
    }
}
