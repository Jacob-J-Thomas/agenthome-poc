namespace EmbodySense.Core.Application.HumanInput.Lifecycle.Models;

/// <summary>Returns one bounded privacy-safe Human Input request lifecycle operation result.</summary>
public sealed partial record HumanInputRequestLifecycleMutationResult
{
    /// <summary>Gets the closed operation outcome.</summary>
    public HumanInputRequestLifecycleMutationStatus Status { get; }
    /// <summary>Gets the exact operation identity, or an empty value for a missing command.</summary>
    public string OperationId { get; }
    /// <summary>Gets the canonical exact-intent hash, or an empty value when it could not be computed.</summary>
    public string RequestHash { get; }
    /// <summary>Gets the durable value-free redacted operation proof when safely established.</summary>
    public HumanInputRequestLifecycleOperationProof? Proof { get; }
    /// <summary>Gets the privacy-safe primary lifecycle projection when safely proved.</summary>
    public HumanInputRequestLifecycleProjection? Primary { get; }
    /// <summary>Gets the privacy-safe related supersession projection when safely proved.</summary>
    public HumanInputRequestLifecycleProjection? Related { get; }
    /// <summary>Gets a delivery opportunity only after exact durable proof.</summary>
    public HumanInputDeliveryOpportunity? DeliveryOpportunity { get; }
    /// <summary>Gets a bounded immutable snapshot of value-free command validation errors.</summary>
    public IReadOnlyList<HumanInputRequestLifecycleMutationValidationError> ValidationErrors { get; }
}
