using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Responses.Models;

namespace EmbodySense.Core.Application.HumanInput.Responses.Models;

/// <summary>Projects value-free immutable response-operation proof without response values, actor, role, or binding data.</summary>
/// <param name="SchemaVersion">The proof schema version.</param>
/// <param name="OperationId">The workspace-global operation identity.</param>
/// <param name="CommandHash">The canonical exact-intent hash.</param>
/// <param name="Kind">The requested response operation.</param>
/// <param name="Outcome">The immutable terminal operation outcome.</param>
/// <param name="FailureCode">The value-free failure classification.</param>
/// <param name="RequestId">The stable target request identity.</param>
/// <param name="RequestVersionId">The exact immutable request version.</param>
/// <param name="PreviousLifecycleVersion">The observed request lifecycle version.</param>
/// <param name="ResultLifecycleVersion">The resulting or unchanged request lifecycle version.</param>
/// <param name="Selection">The opaque durable selection reference when this operation answered the request.</param>
/// <param name="RecordedAtUtc">The trusted UTC evidence instant.</param>
public sealed record HumanInputResponseLifecycleOperationProof(
    int SchemaVersion,
    string OperationId,
    string CommandHash,
    HumanInputResponseOperationKind Kind,
    HumanInputResponseOperationOutcome Outcome,
    HumanInputResponseOperationFailureCode FailureCode,
    string RequestId,
    string RequestVersionId,
    long? PreviousLifecycleVersion,
    long? ResultLifecycleVersion,
    HumanInputResponseSelectionReference? Selection,
    DateTimeOffset RecordedAtUtc);
