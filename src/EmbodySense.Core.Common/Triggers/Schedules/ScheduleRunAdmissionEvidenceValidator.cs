using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Triggers.Models;
using EmbodySense.Core.Common.Triggers.Schedules.Models;

namespace EmbodySense.Core.Common.Triggers.Schedules;

/// <summary>Validates persisted atomic schedule run-admission evidence without granting execution authority.</summary>
public static class ScheduleRunAdmissionEvidenceValidator
{
    /// <summary>Returns whether the evidence is complete, bounded, canonical, hash-bound, and transition-safe.</summary>
    public static bool IsValid(ScheduleRunAdmissionEvidence? evidence)
    {
        if (evidence is null
            || evidence.SchemaVersion != ScheduleRunAdmissionEvidence.CurrentSchemaVersion
            || string.IsNullOrEmpty(evidence.CanonicalEnvelope)
            || System.Text.Encoding.UTF8.GetByteCount(evidence.CanonicalEnvelope) > TriggerDeliveryLimits.MaxCanonicalDocumentUtf8Bytes
            || !TriggerDeliveryJson.TryDeserialize(evidence.CanonicalEnvelope, out var envelope, out _)
            || envelope?.ScheduleExecutionDirective is null
            || !TriggerDeliveryJson.TrySerialize(envelope, out var canonicalEnvelope, out _)
            || !string.Equals(canonicalEnvelope, evidence.CanonicalEnvelope, StringComparison.Ordinal)
            || !TriggerDeliveryHash.TryCompute(envelope, out var envelopeHash, out _)
            || !string.Equals(envelopeHash, evidence.CanonicalEnvelopeHash, StringComparison.Ordinal)
            || !string.Equals(envelope.Loop.LoopId, evidence.LoopId, StringComparison.Ordinal)
            || !CustomLoopArtifactIdentifier.IsValid(evidence.LoopId)
            || evidence.Attempts is null
            || evidence.Attempts.Count is < 1 or > ScheduleRunAdmissionEvidenceLimits.MaxAttempts
            || !ScheduleRunAdmissionEvidenceHash.Matches(evidence))
        {
            return false;
        }

        var directive = envelope.ScheduleExecutionDirective;
        var terminal = false;
        DateTimeOffset? priorRecordedAtUtc = null;
        string? admissionOperationId = null;
        string? candidateRunId = null;
        for (var index = 0; index < evidence.Attempts.Count; index++)
        {
            var attempt = evidence.Attempts[index];
            if (attempt is null
                || attempt.SchemaVersion != ScheduleRunAdmissionAttempt.CurrentSchemaVersion
                || attempt.Ordinal != index + 1
                || !Enum.IsDefined(attempt.Disposition)
                || attempt.Disposition == ScheduleRunAdmissionDisposition.Unknown
                || !CustomLoopArtifactIdentifier.IsValid(attempt.AdmissionOperationId, CustomLoopLimits.MaxMutationOperationIdCharacters)
                || !CustomLoopArtifactIdentifier.IsValid(attempt.CandidateRunId)
                || attempt.RecordedAtUtc.Offset != TimeSpan.Zero
                || priorRecordedAtUtc is not null && attempt.RecordedAtUtc < priorRecordedAtUtc
                || admissionOperationId is not null && !string.Equals(attempt.AdmissionOperationId, admissionOperationId, StringComparison.Ordinal)
                || candidateRunId is not null && !string.Equals(attempt.CandidateRunId, candidateRunId, StringComparison.Ordinal)
                || !DispositionMatchesPolicy(directive.Overlap, attempt.Disposition)
                || terminal)
            {
                return false;
            }

            var created = attempt.Disposition == ScheduleRunAdmissionDisposition.RunCreated;
            if (created != (attempt.BlockingRunId is null)
                || attempt.BlockingRunId is not null && !CustomLoopArtifactIdentifier.IsValid(attempt.BlockingRunId))
            {
                return false;
            }

            terminal = attempt.Disposition is ScheduleRunAdmissionDisposition.RunCreated
                or ScheduleRunAdmissionDisposition.OverlapSkipped
                or ScheduleRunAdmissionDisposition.DeferredOneSuppressed;
            priorRecordedAtUtc = attempt.RecordedAtUtc;
            admissionOperationId = attempt.AdmissionOperationId;
            candidateRunId = attempt.CandidateRunId;
        }

        return true;
    }

    private static bool DispositionMatchesPolicy(
        ScheduleOverlapPolicy overlap,
        ScheduleRunAdmissionDisposition disposition)
        => disposition == ScheduleRunAdmissionDisposition.RunCreated
            || overlap switch
            {
                ScheduleOverlapPolicy.Skip => disposition == ScheduleRunAdmissionDisposition.OverlapSkipped,
                ScheduleOverlapPolicy.DeferOne => disposition is ScheduleRunAdmissionDisposition.OverlapDeferred
                    or ScheduleRunAdmissionDisposition.DeferredOneSuppressed,
                ScheduleOverlapPolicy.Allow => disposition == ScheduleRunAdmissionDisposition.OverlapSerialized,
                _ => false,
            };
}
