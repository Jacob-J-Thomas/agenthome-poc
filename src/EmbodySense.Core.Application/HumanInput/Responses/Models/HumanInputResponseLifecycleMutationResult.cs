namespace EmbodySense.Core.Application.HumanInput.Responses.Models;

/// <summary>Returns one privacy-safe response-operation outcome.</summary>
public sealed partial record HumanInputResponseLifecycleMutationResult
{
    /// <summary>Gets the closed operation status.</summary>
    public HumanInputResponseLifecycleMutationStatus Status { get; }

    /// <summary>Gets the operation identity when safely captured.</summary>
    public string OperationId { get; }

    /// <summary>Gets the canonical exact-intent hash when safely established.</summary>
    public string CommandHash { get; }

    /// <summary>Gets value-free durable operation proof when available.</summary>
    public HumanInputResponseLifecycleOperationProof? Operation { get; }

    /// <summary>Gets privacy-safe current response posture when scope can be proved.</summary>
    public HumanInputResponseLifecycleProjection? Projection { get; }

    /// <summary>Gets an immutable snapshot of deterministic command-envelope errors.</summary>
    public IReadOnlyList<HumanInputResponseLifecycleMutationValidationError> ValidationErrors { get; }
}
