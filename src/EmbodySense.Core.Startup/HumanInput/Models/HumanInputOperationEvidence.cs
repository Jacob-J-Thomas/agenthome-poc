namespace EmbodySense.Core.Startup.HumanInput.Models;

/// <summary>Projects bounded value-free operation evidence without response values, response hashes, actors, roles, routing, grants, or authority evidence.</summary>
/// <param name="OperationId">The exact durable operation identity.</param>
/// <param name="Kind">The stable operation kind token.</param>
/// <param name="Outcome">The stable durable operation-outcome token.</param>
/// <param name="FailureCode">The stable value-free failure classification token.</param>
/// <param name="RequestId">The stable target request identity.</param>
/// <param name="PreviousLifecycleVersion">The lifecycle version observed before the operation, when one existed.</param>
/// <param name="ResultLifecycleVersion">The lifecycle version after the operation, when safely established.</param>
/// <param name="RecordedAtUtc">The trusted durable operation-recording instant.</param>
public sealed record HumanInputOperationEvidence(
    string OperationId,
    string Kind,
    string Outcome,
    string FailureCode,
    string RequestId,
    long? PreviousLifecycleVersion,
    long? ResultLifecycleVersion,
    DateTimeOffset RecordedAtUtc);
