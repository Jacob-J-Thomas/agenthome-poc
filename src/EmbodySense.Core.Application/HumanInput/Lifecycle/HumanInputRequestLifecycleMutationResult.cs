namespace EmbodySense.Core.Application.HumanInput.Lifecycle.Models;

public sealed partial record HumanInputRequestLifecycleMutationResult
{
    /// <summary>Creates one immutable result snapshot.</summary>
    /// <param name="status">The bounded lifecycle mutation outcome.</param>
    /// <param name="operationId">The stable operation identifier.</param>
    /// <param name="requestHash">The canonical hash of the exact requested mutation.</param>
    /// <param name="proof">The durable operation proof, when one is available.</param>
    /// <param name="primary">The privacy-safe primary request projection, when one is in scope.</param>
    /// <param name="related">The privacy-safe related request projection, when one is in scope.</param>
    /// <param name="deliveryOpportunity">The redacted post-persistence delivery opportunity, when delivery is allowed.</param>
    /// <param name="validationErrors">The bounded validation errors for a rejected request.</param>
    public HumanInputRequestLifecycleMutationResult(
        HumanInputRequestLifecycleMutationStatus status,
        string operationId,
        string requestHash,
        HumanInputRequestLifecycleOperationProof? proof,
        HumanInputRequestLifecycleProjection? primary,
        HumanInputRequestLifecycleProjection? related,
        HumanInputDeliveryOpportunity? deliveryOpportunity,
        IEnumerable<HumanInputRequestLifecycleMutationValidationError>? validationErrors = null)
    {
        Status = status;
        OperationId = operationId;
        RequestHash = requestHash;
        Proof = proof;
        Primary = primary;
        Related = related;
        DeliveryOpportunity = deliveryOpportunity;
        ValidationErrors = Array.AsReadOnly((validationErrors ?? []).Take(64).ToArray());
    }
}
