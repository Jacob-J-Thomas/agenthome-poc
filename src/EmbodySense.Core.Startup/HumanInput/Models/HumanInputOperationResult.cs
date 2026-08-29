namespace EmbodySense.Core.Startup.HumanInput.Models;

/// <summary>Returns one bounded privacy-safe Human Input lifecycle or response operation outcome.</summary>
/// <param name="Status">The normalized closed operation status.</param>
/// <param name="OperationId">The exact operation identity when safely captured.</param>
/// <param name="Evidence">The redacted durable operation evidence when safely established.</param>
/// <param name="Request">A fresh canonical redacted posture projection when safely readable.</param>
/// <param name="ValidationErrors">Bounded deterministic malformed-operation errors.</param>
public sealed record HumanInputOperationResult(
    HumanInputOperationStatus Status,
    string OperationId,
    HumanInputOperationEvidence? Evidence,
    HumanInputRequestPosture? Request,
    IReadOnlyList<HumanInputOperationValidationError> ValidationErrors)
{
    /// <summary>Gets a defensive immutable copy of deterministic validation errors.</summary>
    public IReadOnlyList<HumanInputOperationValidationError> ValidationErrors { get; } = ValidationErrors is null ? null! : Array.AsReadOnly(ValidationErrors.ToArray());
}
