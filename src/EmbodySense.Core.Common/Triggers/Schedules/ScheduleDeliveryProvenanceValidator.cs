using EmbodySense.Core.Common.Triggers.Models;
using EmbodySense.Core.Common.Triggers.Schedules.Models;

namespace EmbodySense.Core.Common.Triggers.Schedules;

/// <summary>Validates an authoritative accepted schedule occurrence against one complete trigger envelope.</summary>
public static class ScheduleDeliveryProvenanceValidator
{
    /// <summary>Gets whether every immutable schedule, occurrence, identity, result, and envelope coordinate matches.</summary>
    public static bool Matches(
        ScheduleDeliveryProvenanceEvidence? evidence,
        TriggerDeliveryEnvelope? envelope)
    {
        if (evidence is null
            || envelope is null
            || evidence.SchemaVersion != ScheduleDeliveryProvenanceEvidence.CurrentSchemaVersion
            || !ScheduleContractValidator.ValidateDeliveryResult(evidence.Result).IsValid
            || evidence.Result.Kind is not (ScheduleDeliveryResultKind.Queued or ScheduleDeliveryResultKind.Replayed)
            || !TriggerDeliveryHash.TryCompute(envelope, out var canonicalEnvelopeHash, out _)
            || !string.Equals(canonicalEnvelopeHash, evidence.Result.CanonicalEnvelopeHash, StringComparison.Ordinal)
            || !MatchesCoordinates(
                evidence.Definition,
                evidence.DefinitionHash,
                evidence.Occurrence,
                evidence.Identity,
                envelope)
            || evidence.Result.RecordedAtUtc < envelope.Temporal.ReceivedAtUtc)
        {
            return false;
        }

        return true;
    }

    /// <summary>Gets whether an exact prepared delivery is waiting only for accepted terminal evidence to finalize.</summary>
    public static bool MatchesPendingFinalization(
        ScheduleDefinition? definition,
        string? definitionHash,
        SchedulePendingDelivery? pending,
        TriggerDeliveryEnvelope? envelope)
    {
        if (definition is null
            || pending is null
            || envelope is null
            || pending.Phase != SchedulePendingDeliveryPhase.ResultObserved
            || pending.Prepared is null
            || pending.Result is null
            || !ScheduleContractValidator.ValidatePendingDelivery(pending).IsValid
            || !ScheduleContractValidator.ValidatePreparedDelivery(pending.Prepared).IsValid
            || !ScheduleContractValidator.ValidateDeliveryResult(pending.Result).IsValid
            || pending.Result.Kind is not (ScheduleDeliveryResultKind.Queued or ScheduleDeliveryResultKind.Replayed)
            || !MatchesCoordinates(definition, definitionHash, pending.Occurrence, pending.Identity, envelope)
            || !TriggerDeliveryHash.TryCompute(envelope, out var canonicalEnvelopeHash, out _)
            || !string.Equals(canonicalEnvelopeHash, pending.Prepared.CanonicalEnvelopeHash, StringComparison.Ordinal)
            || !string.Equals(canonicalEnvelopeHash, pending.Result.CanonicalEnvelopeHash, StringComparison.Ordinal)
            || pending.Result.RecordedAtUtc < envelope.Temporal.ReceivedAtUtc
            || !TriggerDeliveryJson.TrySerialize(envelope, out var canonicalEnvelope, out _)
            || !TriggerDeliveryJson.TrySerialize(pending.Prepared.Envelope, out var preparedEnvelope, out _)
            || !string.Equals(canonicalEnvelope, preparedEnvelope, StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    private static bool MatchesCoordinates(
        ScheduleDefinition? definition,
        string? definitionHash,
        ScheduleOccurrence? occurrence,
        ScheduleOccurrenceIdentity? identity,
        TriggerDeliveryEnvelope envelope)
        => definition is not null
            && occurrence is not null
            && identity is not null
            && ScheduleContractValidator.ValidateDefinition(definition).IsValid
            && ScheduleContractValidator.ValidateOccurrence(occurrence).IsValid
            && ScheduleContractHash.TryComputeDefinition(definition, out var computedDefinitionHash, out _)
            && string.Equals(computedDefinitionHash, definitionHash, StringComparison.Ordinal)
            && ScheduleIdentityDerivation.TryDerive(
                definition.ScheduleId,
                definition.Revision,
                definitionHash!,
                occurrence,
                out var expectedIdentity,
                out _)
            && Equals(expectedIdentity, identity)
            && Equals(envelope.DeliveryId, identity.DeliveryId)
            && Equals(envelope.DeduplicationId, identity.DeduplicationId)
            && envelope.Kind == TriggerKind.Time
            && Equals(envelope.Loop, definition.Target)
            && Equals(envelope.Adapter, definition.TimeAdapter)
            && Equals(envelope.ActorContext.ActorId, definition.ActorId)
            && string.Equals(envelope.ActorContext.SurfaceId, definition.SurfaceId, StringComparison.Ordinal)
            && string.Equals(envelope.ActorContext.WorkspaceId, definition.WorkspaceId, StringComparison.Ordinal)
            && string.Equals(envelope.ActorContext.RoleId, definition.RoleId, StringComparison.Ordinal)
            && Equals(envelope.Authority.Profile, definition.AuthorityProfile)
            && envelope.Payload.IsInline
            && envelope.Payload.GovernedReference is null
            && Equals(envelope.Payload.ContentHash, definition.Payload.ContentHash)
            && !envelope.PublicationRequested
            && envelope.InvokingConversation is null
            && envelope.VisibleStatus == TriggerAdmissionStatus.Unknown
            && envelope.VisibleReason == TriggerAdmissionReason.Unknown
            && Equals(occurrence.TimeZone, definition.TimeZone)
            && envelope.Temporal.CreatedAtUtc == occurrence.ScheduledAtUtc
            && envelope.Temporal.AdmittedAtUtc is null
            && envelope.Temporal.NotBeforeUtc is null
            && envelope.Temporal.DeadlineUtc is null
            && envelope.Temporal.ExpiresAtUtc is null
            && envelope.Redelivery.Attempt == 1
            && envelope.Redelivery.Count == 1
            && Equals(envelope.Redelivery.OriginalDeliveryId, envelope.DeliveryId);
}
