using EmbodySense.Core.Common.Loops.Sequential.Models;
using EmbodySense.Core.Common.Triggers;
using EmbodySense.Core.Common.Triggers.Models;
using EmbodySense.Core.Common.Triggers.Schedules;
using EmbodySense.Core.Common.Triggers.Schedules.Models;

namespace EmbodySense.Core.Common.Loops.Sequential;

/// <summary>Creates immutable sequential-run origin evidence from exact canonical trigger deliveries.</summary>
public static class GovernedLoopSequentialTriggerOriginFactory
{
    private const string ScheduleCapabilityId = "org.embodysense/triggers/time";
    private const string ScheduleImplementationId = "triggers/time";
    private const string ScheduleProviderId = "org.embodysense";
    /// <summary>Creates complete provenance only for one exact, canonical schedule-derived time delivery.</summary>
    public static bool TryCreateSchedule(
        TriggerDeliveryEnvelope? envelope,
        ScheduleDeliveryProvenanceEvidence? evidence,
        out GovernedLoopSequentialTriggerOrigin? origin)
    {
        origin = null;
        if (envelope is null
            || evidence is null
            || !ScheduleDeliveryProvenanceValidator.Matches(evidence, envelope)
            || envelope.Kind != TriggerKind.Time
            || envelope.Loop.Kind != TriggerLoopTargetKind.GovernedPublication
            || envelope.Loop.GovernedPublication is null
            || envelope.Loop.AuthorityGrant is null
            || envelope.Loop.LegacyDefinition is not null
            || envelope.PublicationRequested
            || envelope.InvokingConversation is not null
            || !string.Equals(envelope.Adapter.Capability.Id.Value, ScheduleCapabilityId, StringComparison.Ordinal)
            || !string.Equals(envelope.Adapter.Implementation.ProviderId.Value, ScheduleProviderId, StringComparison.Ordinal)
            || !string.Equals(envelope.Adapter.Implementation.ImplementationId, ScheduleImplementationId, StringComparison.Ordinal)
            || !TriggerDeliveryJson.TrySerialize(envelope, out var canonicalEnvelope, out _)
            || !TriggerDeliveryHash.TryCompute(envelope, out var canonicalEnvelopeHash, out _))
        {
            return false;
        }

        origin = new GovernedLoopSequentialTriggerOrigin(
            GovernedLoopSequentialTriggerOrigin.CurrentSchemaVersion,
            evidence.Definition.ScheduleId.Value,
            evidence.Definition.Revision,
            evidence.DefinitionHash,
            ScheduleContractCopy.Copy(evidence.Occurrence)!,
            canonicalEnvelope!,
            canonicalEnvelopeHash!);
        return true;
    }

    /// <summary>Validates the persisted schedule coordinates and canonical envelope without consulting mutable state.</summary>
    public static bool MatchesPersistedOrigin(
        TriggerDeliveryEnvelope? envelope,
        GovernedLoopSequentialTriggerOrigin? origin)
    {
        if (envelope is null
            || origin is null
            || !ScheduleId.TryParse(origin.ScheduleId, out var scheduleId)
            || origin.DefinitionRevision < 1
            || !IsHash(origin.DefinitionHash)
            || !ScheduleContractValidator.ValidateOccurrence(origin.Occurrence).IsValid
            || !ScheduleIdentityDerivation.TryDerive(
                scheduleId,
                origin.DefinitionRevision,
                origin.DefinitionHash,
                origin.Occurrence,
                out var identity,
                out _)
            || !Equals(identity!.DeliveryId, envelope.DeliveryId)
            || !Equals(identity.DeduplicationId, envelope.DeduplicationId)
            || envelope.Kind != TriggerKind.Time
            || envelope.Loop.Kind != TriggerLoopTargetKind.GovernedPublication
            || envelope.Loop.GovernedPublication is null
            || envelope.Loop.AuthorityGrant is null
            || envelope.Loop.LegacyDefinition is not null
            || envelope.PublicationRequested
            || envelope.InvokingConversation is not null
            || envelope.Temporal.CreatedAtUtc != origin.Occurrence.ScheduledAtUtc
            || envelope.Temporal.AdmittedAtUtc is not null
            || envelope.Temporal.NotBeforeUtc is not null
            || envelope.Temporal.DeadlineUtc is not null
            || envelope.Temporal.ExpiresAtUtc is not null
            || envelope.VisibleStatus != TriggerAdmissionStatus.Unknown
            || envelope.VisibleReason != TriggerAdmissionReason.Unknown
            || envelope.Redelivery.Attempt != 1
            || envelope.Redelivery.Count != 1
            || !Equals(envelope.Redelivery.OriginalDeliveryId, envelope.DeliveryId)
            || !string.Equals(envelope.Adapter.Capability.Id.Value, ScheduleCapabilityId, StringComparison.Ordinal)
            || !string.Equals(envelope.Adapter.Implementation.ProviderId.Value, ScheduleProviderId, StringComparison.Ordinal)
            || !string.Equals(envelope.Adapter.Implementation.ImplementationId, ScheduleImplementationId, StringComparison.Ordinal)
            || !TriggerDeliveryJson.TrySerialize(envelope, out var canonicalEnvelope, out _)
            || !TriggerDeliveryHash.TryCompute(envelope, out var canonicalEnvelopeHash, out _))
        {
            return false;
        }

        return string.Equals(canonicalEnvelope, origin.CanonicalEnvelope, StringComparison.Ordinal)
            && string.Equals(canonicalEnvelopeHash, origin.CanonicalEnvelopeHash, StringComparison.Ordinal);
    }

    private static bool IsHash(string? value)
    {
        if (value?.Length != GovernedLoopSequentialContractLimits.Sha256HexCharacters)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }
}
