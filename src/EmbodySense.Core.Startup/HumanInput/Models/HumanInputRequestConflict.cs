namespace EmbodySense.Core.Startup.HumanInput.Models;

/// <summary>Projects one retained value-free canonical conflict without hashes, values, actors, roles, routing, grants, or authority evidence.</summary>
/// <param name="OperationId">The exact durable operation identity that conflicted.</param>
/// <param name="OperationFamily">The stable lifecycle or response operation family.</param>
/// <param name="OperationKind">The stable operation-kind token.</param>
/// <param name="FailureCode">The stable value-free conflict classification token.</param>
/// <param name="RecordedAtUtc">The trusted durable conflict-recording instant.</param>
public sealed record HumanInputRequestConflict(
    string OperationId,
    string OperationFamily,
    string OperationKind,
    string FailureCode,
    DateTimeOffset RecordedAtUtc);
