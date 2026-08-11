using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;

namespace EmbodySense.Core.Application.HumanInput.Lifecycle.Models;

/// <summary>Projects value-free immutable lifecycle-operation proof without actor, reason, grant, authority, private request, routing, or binding data.</summary>
/// <param name="SchemaVersion">The proof schema version.</param>
/// <param name="OperationId">The workspace-global operation identity.</param>
/// <param name="RequestHash">The canonical exact-intent hash.</param>
/// <param name="Kind">The requested lifecycle operation.</param>
/// <param name="Outcome">The immutable terminal operation outcome.</param>
/// <param name="FailureCode">The value-free failure classification.</param>
/// <param name="TargetRequestId">The stable primary lifecycle identity.</param>
/// <param name="PreviousLifecycleVersion">The observed primary lifecycle version, when one existed.</param>
/// <param name="ResultLifecycleVersion">The resulting or unchanged primary lifecycle version, when one existed.</param>
/// <param name="RelatedRequestId">The related replacement lifecycle only for supersede.</param>
/// <param name="RelatedPreviousLifecycleVersion">The observed related lifecycle version, when one existed.</param>
/// <param name="RelatedResultLifecycleVersion">The resulting or unchanged related lifecycle version, when one existed.</param>
/// <param name="RecordedAtUtc">The trusted UTC evidence time.</param>
public sealed record HumanInputRequestLifecycleOperationProof(
    int SchemaVersion,
    string OperationId,
    string RequestHash,
    HumanInputRequestLifecycleOperationKind Kind,
    HumanInputRequestLifecycleOperationOutcome Outcome,
    HumanInputRequestLifecycleOperationFailureCode FailureCode,
    string TargetRequestId,
    long? PreviousLifecycleVersion,
    long? ResultLifecycleVersion,
    string? RelatedRequestId,
    long? RelatedPreviousLifecycleVersion,
    long? RelatedResultLifecycleVersion,
    DateTimeOffset RecordedAtUtc);
