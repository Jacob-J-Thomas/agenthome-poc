using EmbodySense.Core.Application.Loops.Revisions.Models;

namespace EmbodySense.Core.Application.Loops.GraphAuthoring.Models;

/// <summary>Binds a durable pending or terminal graph-authoring intent to optional lifecycle evidence.</summary>
public sealed record GovernedLoopGraphRevisionStoredOperation(
    GovernedLoopGraphRevisionOperationState State,
    string GraphId,
    string OperationId,
    string LifecycleRequestHash,
    string AuthoringRequestHash,
    GovernedLoopRevisionStoredOperation? LifecycleOperation,
    string? GraphValidationEvidenceHash);
