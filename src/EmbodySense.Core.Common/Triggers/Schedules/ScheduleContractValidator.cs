using System.Text;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Triggers.Models;
using EmbodySense.Core.Common.Triggers.Schedules.Models;

namespace EmbodySense.Core.Common.Triggers.Schedules;

/// <summary>Revalidates dependency-free schedule definitions, state, and evidence at public boundaries.</summary>
public static class ScheduleContractValidator
{
    /// <summary>Validates one immutable schedule definition without resolving authority, time, or time-zone rules.</summary>
    public static ScheduleContractValidationResult ValidateDefinition(ScheduleDefinition? definition)
    {
        var errors = new List<ScheduleContractError>();
        if (definition is null)
        {
            return Result(Error("required", "$"));
        }

        ValidateSchema(definition.SchemaVersion, "schemaVersion", errors);
        ValidateScheduleId(definition.ScheduleId, "scheduleId", errors);
        ValidateRevision(definition.Revision, "revision", errors);
        var targetValidation = TriggerDeliveryValidator.ValidateLoopReference(definition.Target);
        if (!targetValidation.IsValid || definition.Target?.Kind != TriggerLoopTargetKind.GovernedPublication)
        {
            errors.Add(Error("governed_target_required", "target"));
        }

        var adapterValidation = TriggerDeliveryValidator.ValidateAdapterReference(definition.TimeAdapter);
        if (!adapterValidation.IsValid)
        {
            errors.Add(Error("invalid_time_adapter", "timeAdapter"));
        }

        if (definition.ActorId is null || !AuthorityActorId.TryParse(definition.ActorId.Value, out _, out _))
        {
            errors.Add(Error("invalid_actor", "actorId"));
        }

        ValidateArtifactId(definition.SurfaceId, TriggerDeliveryLimits.MaxSurfaceIdCharacters, "surfaceId", errors);
        ValidateArtifactId(definition.WorkspaceId, TriggerDeliveryLimits.MaxWorkspaceIdCharacters, "workspaceId", errors);
        ValidateArtifactId(definition.RoleId, TriggerDeliveryLimits.MaxRoleIdCharacters, "roleId", errors);
        ValidateAuthorityProfile(definition.AuthorityProfile, "authorityProfile", errors);
        ValidatePayload(definition.Payload, "payload", errors);

        if (!IsDefined(definition.Priority))
        {
            errors.Add(Error("unsupported_priority", "priority"));
        }

        errors.AddRange(ValidateRecurrence(definition.Recurrence, "recurrence"));
        errors.AddRange(ValidateTimeZone(definition.TimeZone, "timeZone"));
        if (definition.DaylightSaving is null
            || !IsDefined(definition.DaylightSaving.InvalidLocalTime)
            || !IsDefined(definition.DaylightSaving.AmbiguousLocalTime))
        {
            errors.Add(Error("invalid_daylight_saving_policy", "daylightSaving"));
        }

        errors.AddRange(ValidateMisfire(definition.Misfire, "misfire"));
        if (!IsDefined(definition.Overlap))
        {
            errors.Add(Error("unsupported_overlap_policy", "overlap"));
        }

        return Result(errors);
    }

    /// <summary>Validates one exact local and UTC occurrence without calculating its time-zone mapping.</summary>
    public static ScheduleContractValidationResult ValidateOccurrence(ScheduleOccurrence? occurrence)
    {
        var errors = new List<ScheduleContractError>();
        if (occurrence is null)
        {
            return Result(Error("required", "$"));
        }

        ValidateSchema(occurrence.SchemaVersion, "schemaVersion", errors);
        ValidateOrdinal(occurrence.Ordinal, "ordinal", errors);
        ValidateLocal(occurrence.ScheduledLocal, "scheduledLocal", errors);
        ValidateUtc(occurrence.ScheduledAtUtc, "scheduledAtUtc", errors);
        errors.AddRange(ValidateTimeZone(occurrence.TimeZone, "timeZone"));
        return Result(errors);
    }

    /// <summary>Validates one pending delivery and optional exact queue-result evidence.</summary>
    public static ScheduleContractValidationResult ValidatePendingDelivery(SchedulePendingDelivery? pending)
    {
        var errors = new List<ScheduleContractError>();
        if (pending is null)
        {
            return Result(Error("required", "$"));
        }

        ValidateSchema(pending.SchemaVersion, "schemaVersion", errors);
        if (!IsDefined(pending.Phase))
        {
            errors.Add(Error("unsupported_pending_phase", "phase"));
        }

        AddNested(errors, ValidateOccurrence(pending.Occurrence), "occurrence");
        ValidateIdentityShape(pending.Identity, "identity", errors);
        if (pending.ClaimId is null || !ScheduleClaimId.TryParse(pending.ClaimId.Value, out _))
        {
            errors.Add(Error("invalid_claim_id", "claimId"));
        }

        ValidateUtc(pending.ClaimedAtUtc, "claimedAtUtc", errors);
        if (IsUtc(pending.ClaimedAtUtc)
            && pending.Occurrence is not null
            && IsUtc(pending.Occurrence.ScheduledAtUtc)
            && pending.ClaimedAtUtc < pending.Occurrence.ScheduledAtUtc)
        {
            errors.Add(Error("claim_before_occurrence", "claimedAtUtc"));
        }

        if (pending.Prepared is not null)
        {
            AddNested(errors, ValidatePreparedDelivery(pending.Prepared), "prepared");
            if (pending.Identity is not null
                && pending.Prepared.Envelope is not null
                && (!Equals(pending.Identity.DeliveryId, pending.Prepared.Envelope.DeliveryId)
                    || !Equals(pending.Identity.DeduplicationId, pending.Prepared.Envelope.DeduplicationId)))
            {
                errors.Add(Error("prepared_identity_mismatch", "prepared.envelope"));
            }

            if (IsUtc(pending.Prepared.PreparedAtUtc)
                && IsUtc(pending.ClaimedAtUtc)
                && pending.Prepared.PreparedAtUtc < pending.ClaimedAtUtc)
            {
                errors.Add(Error("prepared_before_claim", "prepared.preparedAtUtc"));
            }
        }

        var hasCurrentEvidenceHash = pending.CurrentEvidenceHash is not null;
        var hasRecurrenceProofHash = pending.RecurrenceProofHash is not null;
        var hasOverlapEvidenceHash = pending.OverlapEvidenceHash is not null;
        var hasFinalizationPlan = pending.FinalizationPlan is not null;
        var hasPreparedEnvelope = pending.Prepared is not null;
        if (new[] { hasCurrentEvidenceHash, hasRecurrenceProofHash, hasOverlapEvidenceHash, hasFinalizationPlan, hasPreparedEnvelope }.Distinct().Count() != 1)
        {
            errors.Add(Error("incomplete_preparation", "prepared"));
        }

        if (hasPreparedEnvelope)
        {
            if (!IsSha256(pending.CurrentEvidenceHash))
            {
                errors.Add(Error("invalid_hash", "currentEvidenceHash"));
            }

            if (!IsSha256(pending.RecurrenceProofHash))
            {
                errors.Add(Error("invalid_hash", "recurrenceProofHash"));
            }

            if (!IsSha256(pending.OverlapEvidenceHash))
            {
                errors.Add(Error("invalid_hash", "overlapEvidenceHash"));
            }

            AddNested(errors, ValidateFinalizationPlan(pending.FinalizationPlan), "finalizationPlan");
            ValidateFinalizationPlanAgainstOccurrence(pending.FinalizationPlan, pending.Occurrence, "finalizationPlan", errors);
            if (pending.Prepared?.Envelope?.Temporal is { } temporal
                && pending.Occurrence is not null
                && temporal.CreatedAtUtc != pending.Occurrence.ScheduledAtUtc)
            {
                errors.Add(Error("prepared_occurrence_mismatch", "prepared.envelope.temporal.createdAtUtc"));
            }
        }

        if (pending.Result is not null)
        {
            AddNested(errors, ValidateDeliveryResult(pending.Result), "result");
            if (pending.Prepared is null)
            {
                errors.Add(Error("prepared_delivery_required", "prepared"));
            }
            else if (!string.Equals(pending.Result.CanonicalEnvelopeHash, pending.Prepared.CanonicalEnvelopeHash, StringComparison.Ordinal))
            {
                errors.Add(Error("result_envelope_hash_mismatch", "result.canonicalEnvelopeHash"));
            }

            if (IsUtc(pending.Result.RecordedAtUtc)
                && IsUtc(pending.ClaimedAtUtc)
                && pending.Result.RecordedAtUtc < pending.ClaimedAtUtc)
            {
                errors.Add(Error("result_before_claim", "result.recordedAtUtc"));
            }

            if (pending.Prepared is not null
                && IsUtc(pending.Result.RecordedAtUtc)
                && IsUtc(pending.Prepared.PreparedAtUtc)
                && pending.Result.RecordedAtUtc < pending.Prepared.PreparedAtUtc)
            {
                errors.Add(Error("result_before_prepared", "result.recordedAtUtc"));
            }
        }

        var shapeMatchesPhase = pending.Phase switch
        {
            SchedulePendingDeliveryPhase.Claimed => !hasPreparedEnvelope && pending.Result is null,
            SchedulePendingDeliveryPhase.Prepared => hasPreparedEnvelope && pending.Result is null,
            SchedulePendingDeliveryPhase.ResultObserved => hasPreparedEnvelope && pending.Result is not null,
            _ => false,
        };
        if (!shapeMatchesPhase)
        {
            errors.Add(Error("pending_phase_shape_mismatch", "phase"));
        }

        return Result(errors);
    }

    /// <summary>Validates one immutable successor plan without recalculating recurrence.</summary>
    public static ScheduleContractValidationResult ValidateFinalizationPlan(ScheduleFinalizationPlan? plan)
    {
        var errors = new List<ScheduleContractError>();
        if (plan is null)
        {
            return Result(Error("required", "$"));
        }

        ValidateSchema(plan.SchemaVersion, "schemaVersion", errors);
        if (plan.NextOccurrence is not null)
        {
            AddNested(errors, ValidateOccurrence(plan.NextOccurrence), "nextOccurrence");
        }

        ValidateCatchUpEpisode(plan.CatchUpEpisode, plan.NextOccurrence, "catchUpEpisode", errors);
        ValidateDeferredOccurrence(plan.DeferredOccurrence, "deferredOccurrence", errors);
        if (plan.DeferredOccurrence is not null
            && (plan.NextOccurrence is null || !Equals(plan.DeferredOccurrence.Occurrence, plan.NextOccurrence)))
        {
            errors.Add(Error("deferred_occurrence_mismatch", "deferredOccurrence.occurrence"));
        }

        ValidateDispositionCollection(
            plan.DispositionEvidence,
            ScheduleContractLimits.MaxFinalizationEvidenceItems,
            "dispositionEvidence",
            errors);
        ValidateDeferredDisposition(plan, errors);
        return Result(errors);
    }

    /// <summary>Validates the exact envelope snapshot persisted before queue admission.</summary>
    public static ScheduleContractValidationResult ValidatePreparedDelivery(SchedulePreparedDelivery? prepared)
    {
        var errors = new List<ScheduleContractError>();
        if (prepared is null)
        {
            return Result(Error("required", "$"));
        }

        ValidateSchema(prepared.SchemaVersion, "schemaVersion", errors);
        if (!TriggerDeliveryHash.TryCompute(prepared.Envelope, out var computedHash, out _))
        {
            errors.Add(Error("invalid_envelope", "envelope"));
        }
        else if (!string.Equals(computedHash, prepared.CanonicalEnvelopeHash, StringComparison.Ordinal))
        {
            errors.Add(Error("envelope_hash_mismatch", "canonicalEnvelopeHash"));
        }

        if (prepared.Envelope is not null && prepared.Envelope.Kind != TriggerKind.Time)
        {
            errors.Add(Error("time_trigger_required", "envelope.kind"));
        }

        var inlinePayload = prepared.Envelope?.Payload?.GetInlinePayload();
        if (inlinePayload is null)
        {
            errors.Add(Error("inline_payload_required", "envelope.payload"));
        }
        else if (inlinePayload.Length > TriggerDeliveryLimits.MaxInlinePayloadBytes
            || !Equals(CapabilityIntegrityDigest.Compute(inlinePayload), prepared.Envelope!.Payload.ContentHash))
        {
            errors.Add(Error("invalid_inline_payload", "envelope.payload"));
        }
        else if (!IsStrictUtf8(inlinePayload))
        {
            errors.Add(Error("invalid_utf8_payload", "envelope.payload"));
        }

        if (prepared.Envelope is not null
            && (prepared.Envelope.VisibleStatus != TriggerAdmissionStatus.Unknown
                || prepared.Envelope.VisibleReason != TriggerAdmissionReason.Unknown
                || prepared.Envelope.Temporal?.AdmittedAtUtc is not null))
        {
            errors.Add(Error("preadmission_envelope_required", "envelope.visibleStatus"));
        }

        ValidateUtc(prepared.PreparedAtUtc, "preparedAtUtc", errors);
        if (prepared.Envelope?.Temporal is { } temporal
            && IsUtc(prepared.PreparedAtUtc)
            && prepared.PreparedAtUtc < temporal.ReceivedAtUtc)
        {
            errors.Add(Error("prepared_before_received", "preparedAtUtc"));
        }

        return Result(errors);
    }

    /// <summary>Validates one exact definition and state composition in every durable state phase.</summary>
    public static ScheduleContractValidationResult ValidateDefinitionStateComposition(
        ScheduleDefinition? definition,
        ScheduleState? state)
    {
        var errors = new List<ScheduleContractError>();
        var definitionValidation = ValidateDefinition(definition);
        var stateValidation = ValidateState(state);
        AddNested(errors, definitionValidation, "definition");
        AddNested(errors, stateValidation, "state");
        if (!definitionValidation.IsValid || !stateValidation.IsValid)
        {
            return Result(errors);
        }

        var validDefinition = definition!;
        var validState = state!;
        if (!ScheduleContractHash.TryComputeDefinition(validDefinition, out var definitionHash, out _)
            || !Equals(validState.ScheduleId, validDefinition.ScheduleId)
            || validState.DefinitionRevision != validDefinition.Revision
            || !string.Equals(validState.DefinitionHash, definitionHash, StringComparison.Ordinal))
        {
            errors.Add(Error("definition_state_mismatch", "state.definitionHash"));
        }

        if (validState.Enabled && !validDefinition.Enabled)
        {
            errors.Add(Error("definition_disabled", "state.enabled"));
        }

        ValidateDefinitionBoundState(validDefinition, validState, errors);

        if (validState.PendingDelivery?.FinalizationPlan is { } finalizationPlan)
        {
            ValidateRecurrenceSuccessor(
                validDefinition.Recurrence,
                validState.PendingDelivery.Occurrence,
                finalizationPlan,
                errors);
        }

        return Result(errors);
    }

    /// <summary>Validates the exact definition, state, and prepared-envelope composition without resolving current external evidence.</summary>
    public static ScheduleContractValidationResult ValidatePreparedDeliveryComposition(
        ScheduleDefinition? definition,
        ScheduleState? state)
    {
        var errors = new List<ScheduleContractError>();
        var compositionValidation = ValidateDefinitionStateComposition(definition, state);
        errors.AddRange(compositionValidation.Errors);
        if (!compositionValidation.IsValid)
        {
            return Result(errors);
        }

        if (state!.PendingDelivery?.Prepared?.Envelope is not { } envelope)
        {
            errors.Add(Error("prepared_delivery_required", "state.pendingDelivery.prepared"));
            return Result(errors);
        }

        if (!Equals(envelope.Loop, definition!.Target))
        {
            errors.Add(Error("target_mismatch", "state.pendingDelivery.prepared.envelope.loop"));
        }

        if (!Equals(envelope.Adapter, definition.TimeAdapter))
        {
            errors.Add(Error("adapter_mismatch", "state.pendingDelivery.prepared.envelope.adapter"));
        }

        if (envelope.ActorContext is null
            || !Equals(envelope.ActorContext.ActorId, definition.ActorId)
            || !string.Equals(envelope.ActorContext.SurfaceId, definition.SurfaceId, StringComparison.Ordinal)
            || !string.Equals(envelope.ActorContext.WorkspaceId, definition.WorkspaceId, StringComparison.Ordinal)
            || !string.Equals(envelope.ActorContext.RoleId, definition.RoleId, StringComparison.Ordinal))
        {
            errors.Add(Error("actor_context_mismatch", "state.pendingDelivery.prepared.envelope.actorContext"));
        }

        if (envelope.Authority is null || !Equals(envelope.Authority.Profile, definition.AuthorityProfile))
        {
            errors.Add(Error("authority_profile_mismatch", "state.pendingDelivery.prepared.envelope.authority.profile"));
        }

        if (envelope.Payload is null
            || !envelope.Payload.IsInline
            || envelope.Payload.GovernedReference is not null
            || !Equals(envelope.Payload.ContentHash, definition.Payload.ContentHash))
        {
            errors.Add(Error("payload_mismatch", "state.pendingDelivery.prepared.envelope.payload"));
        }

        if (envelope.PublicationRequested)
        {
            errors.Add(Error("publication_not_supported", "state.pendingDelivery.prepared.envelope.publicationRequested"));
        }

        if (envelope.InvokingConversation is not null)
        {
            errors.Add(Error("invoking_conversation_not_supported", "state.pendingDelivery.prepared.envelope.invokingConversation"));
        }

        if (envelope.Temporal.NotBeforeUtc is not null)
        {
            errors.Add(Error("temporal_gate_not_supported", "state.pendingDelivery.prepared.envelope.temporal.notBeforeUtc"));
        }

        if (envelope.Temporal.DeadlineUtc is not null)
        {
            errors.Add(Error("temporal_gate_not_supported", "state.pendingDelivery.prepared.envelope.temporal.deadlineUtc"));
        }

        if (envelope.Temporal.ExpiresAtUtc is not null)
        {
            errors.Add(Error("temporal_gate_not_supported", "state.pendingDelivery.prepared.envelope.temporal.expiresAtUtc"));
        }

        if (envelope.Redelivery.Attempt != 1
            || envelope.Redelivery.Count != 1
            || !Equals(envelope.Redelivery.OriginalDeliveryId, envelope.DeliveryId))
        {
            errors.Add(Error("initial_redelivery_required", "state.pendingDelivery.prepared.envelope.redelivery"));
        }

        return Result(errors);
    }

    /// <summary>Validates one queue-result evidence item without interpreting it as proof of delivery.</summary>
    public static ScheduleContractValidationResult ValidateDeliveryResult(ScheduleDeliveryResultEvidence? result)
    {
        var errors = new List<ScheduleContractError>();
        if (result is null)
        {
            return Result(Error("required", "$"));
        }

        ValidateSchema(result.SchemaVersion, "schemaVersion", errors);
        if (!IsDefined(result.Kind))
        {
            errors.Add(Error("unsupported_delivery_result", "kind"));
        }

        ValidateReason(result.ReasonCode, "reasonCode", errors);
        if (!IsSha256(result.CanonicalEnvelopeHash))
        {
            errors.Add(Error("invalid_hash", "canonicalEnvelopeHash"));
        }

        ValidateUtc(result.RecordedAtUtc, "recordedAtUtc", errors);
        return Result(errors);
    }

    /// <summary>Validates one conclusive delivery result retained after pending finalization.</summary>
    public static ScheduleContractValidationResult ValidateTerminalDeliveryEvidence(ScheduleTerminalDeliveryEvidence? evidence)
    {
        var errors = new List<ScheduleContractError>();
        if (evidence is null)
        {
            return Result(Error("required", "$"));
        }

        ValidateSchema(evidence.SchemaVersion, "schemaVersion", errors);
        AddNested(errors, ValidateOccurrence(evidence.Occurrence), "occurrence");
        ValidateIdentityShape(evidence.Identity, "identity", errors);
        if (!IsSha256(evidence.CurrentEvidenceHash))
        {
            errors.Add(Error("invalid_hash", "currentEvidenceHash"));
        }

        if (!IsSha256(evidence.RecurrenceProofHash))
        {
            errors.Add(Error("invalid_hash", "recurrenceProofHash"));
        }

        if (!IsSha256(evidence.OverlapEvidenceHash))
        {
            errors.Add(Error("invalid_hash", "overlapEvidenceHash"));
        }

        AddNested(errors, ValidateDeliveryResult(evidence.Result), "result");
        if (evidence.Result is not null && !IsTerminal(evidence.Result.Kind))
        {
            errors.Add(Error("nonterminal_delivery_result", "result.kind"));
        }

        ValidateUtc(evidence.FinalizedAtUtc, "finalizedAtUtc", errors);
        if (evidence.Result is not null
            && IsUtc(evidence.Result.RecordedAtUtc)
            && IsUtc(evidence.FinalizedAtUtc)
            && evidence.FinalizedAtUtc < evidence.Result.RecordedAtUtc)
        {
            errors.Add(Error("finalized_before_result", "finalizedAtUtc"));
        }

        if (evidence.Result is not null
            && evidence.Occurrence is not null
            && IsUtc(evidence.Result.RecordedAtUtc)
            && IsUtc(evidence.Occurrence.ScheduledAtUtc)
            && evidence.Result.RecordedAtUtc < evidence.Occurrence.ScheduledAtUtc)
        {
            errors.Add(Error("result_before_occurrence", "result.recordedAtUtc"));
        }

        return Result(errors);
    }

    /// <summary>Validates one bounded skipped/deferred occurrence evidence item.</summary>
    public static ScheduleContractValidationResult ValidateDispositionEvidence(ScheduleOccurrenceDispositionEvidence? evidence)
    {
        var errors = new List<ScheduleContractError>();
        if (evidence is null)
        {
            return Result(Error("required", "$"));
        }

        ValidateSchema(evidence.SchemaVersion, "schemaVersion", errors);
        ValidateOrdinal(evidence.FirstOrdinal, "firstOrdinal", errors);
        ValidateOrdinal(evidence.LastOrdinal, "lastOrdinal", errors);
        var boundedOrdinals = evidence.FirstOrdinal is >= 1 and <= ScheduleContractLimits.MaxOccurrenceOrdinal
            && evidence.LastOrdinal is >= 1 and <= ScheduleContractLimits.MaxOccurrenceOrdinal;
        if (!boundedOrdinals
            || evidence.FirstOrdinal > evidence.LastOrdinal
            || evidence.Count != evidence.LastOrdinal - evidence.FirstOrdinal + 1)
        {
            errors.Add(Error("invalid_disposition_range", "count"));
        }

        ValidateLocal(evidence.FirstScheduledLocal, "firstScheduledLocal", errors);
        ValidateLocal(evidence.LastScheduledLocal, "lastScheduledLocal", errors);
        if (evidence.Count == 1 && evidence.FirstScheduledLocal != evidence.LastScheduledLocal)
        {
            errors.Add(Error("invalid_disposition_range", "lastScheduledLocal"));
        }

        errors.AddRange(ValidateTimeZone(evidence.TimeZone, "timeZone"));
        if (!IsDefined(evidence.Disposition))
        {
            errors.Add(Error("unsupported_disposition", "disposition"));
        }

        var overlapDecision = evidence.Disposition is ScheduleOccurrenceDisposition.OverlapSkipped
            or ScheduleOccurrenceDisposition.OverlapDeferred;
        if (overlapDecision && !IsSha256(evidence.DecisionEvidenceHash))
        {
            errors.Add(Error("decision_evidence_required", "decisionEvidenceHash"));
        }
        else if (!overlapDecision && evidence.DecisionEvidenceHash is not null && !IsSha256(evidence.DecisionEvidenceHash))
        {
            errors.Add(Error("invalid_hash", "decisionEvidenceHash"));
        }

        if (evidence.Disposition != ScheduleOccurrenceDisposition.MisfireSkipped && evidence.Count != 1)
        {
            errors.Add(Error("singleton_disposition_required", "count"));
        }

        var utcMustBeAbsent = evidence.Disposition == ScheduleOccurrenceDisposition.InvalidLocalTimeSkipped;
        if (utcMustBeAbsent != (evidence.FirstScheduledAtUtc is null)
            || utcMustBeAbsent != (evidence.LastScheduledAtUtc is null))
        {
            errors.Add(Error("invalid_disposition_utc", "firstScheduledAtUtc"));
        }
        else if (evidence.FirstScheduledAtUtc is { } firstScheduledAtUtc
            && evidence.LastScheduledAtUtc is { } lastScheduledAtUtc)
        {
            ValidateUtc(firstScheduledAtUtc, "firstScheduledAtUtc", errors);
            ValidateUtc(lastScheduledAtUtc, "lastScheduledAtUtc", errors);
            if (evidence.Count == 1 && firstScheduledAtUtc != lastScheduledAtUtc
                || evidence.Count > 1 && firstScheduledAtUtc >= lastScheduledAtUtc)
            {
                errors.Add(Error("invalid_disposition_range", "lastScheduledAtUtc"));
            }
        }

        ValidateReason(evidence.ReasonCode, "reasonCode", errors);
        ValidateUtc(evidence.RecordedAtUtc, "recordedAtUtc", errors);
        if (evidence.LastScheduledAtUtc is { } scheduled
            && IsUtc(scheduled)
            && IsUtc(evidence.RecordedAtUtc)
            && evidence.RecordedAtUtc < scheduled)
        {
            errors.Add(Error("disposition_before_occurrence", "recordedAtUtc"));
        }

        return Result(errors);
    }

    /// <summary>Validates one optimistic state snapshot, including exact pending identity replay coordinates.</summary>
    public static ScheduleContractValidationResult ValidateState(ScheduleState? state)
    {
        var errors = new List<ScheduleContractError>();
        if (state is null)
        {
            return Result(Error("required", "$"));
        }

        ValidateSchema(state.SchemaVersion, "schemaVersion", errors);
        ValidateScheduleId(state.ScheduleId, "scheduleId", errors);
        ValidateRevision(state.DefinitionRevision, "definitionRevision", errors);
        if (!IsSha256(state.DefinitionHash))
        {
            errors.Add(Error("invalid_hash", "definitionHash"));
        }

        ValidateRevision(state.StateRevision, "stateRevision", errors);
        if (state.NextOccurrence is not null)
        {
            AddNested(errors, ValidateOccurrence(state.NextOccurrence), "nextOccurrence");
        }

        ValidateCatchUpEpisode(state.CatchUpEpisode, state.NextOccurrence, "catchUpEpisode", errors);
        ValidateDeferredOccurrence(state.DeferredOccurrence, "deferredOccurrence", errors);
        if (state.DeferredOccurrence is not null)
        {
            if (state.NextOccurrence is null || !Equals(state.DeferredOccurrence.Occurrence, state.NextOccurrence))
            {
                errors.Add(Error("deferred_occurrence_mismatch", "deferredOccurrence.occurrence"));
            }

            if (ScheduleIdentityDerivation.TryDerive(
                    state.ScheduleId,
                    state.DefinitionRevision,
                    state.DefinitionHash,
                    state.DeferredOccurrence.Occurrence,
                    out var deferredIdentity,
                    out _)
                && !Equals(deferredIdentity, state.DeferredOccurrence.Identity))
            {
                errors.Add(Error("deferred_identity_mismatch", "deferredOccurrence.identity"));
            }
        }

        if (state.LastClockObservedAtUtc is { } observedAtUtc)
        {
            ValidateUtc(observedAtUtc, "lastClockObservedAtUtc", errors);
        }

        if (state.PendingDelivery is not null)
        {
            AddNested(errors, ValidatePendingDelivery(state.PendingDelivery), "pendingDelivery");
            if (state.NextOccurrence is null || !Equals(state.NextOccurrence, state.PendingDelivery.Occurrence))
            {
                errors.Add(Error("pending_occurrence_mismatch", "pendingDelivery.occurrence"));
            }

            if (state.LastClockObservedAtUtc is null
                || IsUtc(state.LastClockObservedAtUtc.Value)
                    && IsUtc(state.PendingDelivery.ClaimedAtUtc)
                    && state.PendingDelivery.ClaimedAtUtc > state.LastClockObservedAtUtc.Value)
            {
                errors.Add(Error("pending_claim_after_clock", "pendingDelivery.claimedAtUtc"));
            }

            if (state.ScheduleId is not null
                && IsRevision(state.DefinitionRevision)
                && IsSha256(state.DefinitionHash)
                && ScheduleIdentityDerivation.TryDerive(
                    state.ScheduleId,
                    state.DefinitionRevision,
                    state.DefinitionHash,
                    state.PendingDelivery.Occurrence,
                    out var expected,
                    out _)
                && !Equals(expected, state.PendingDelivery.Identity))
            {
                errors.Add(Error("pending_identity_mismatch", "pendingDelivery.identity"));
            }

            if (state.DeferredOccurrence is not null
                && (!Equals(state.DeferredOccurrence.Occurrence, state.PendingDelivery.Occurrence)
                    || !Equals(state.DeferredOccurrence.Identity, state.PendingDelivery.Identity)))
            {
                errors.Add(Error("deferred_pending_mismatch", "pendingDelivery"));
            }
        }

        ValidateDispositionCollection(
            state.DispositionEvidence,
            ScheduleContractLimits.MaxDispositionEvidenceItems,
            "dispositionEvidence",
            errors);
        ValidateStateDispositionCoordinates(state, errors);
        ValidateTerminalCollection(state, errors);
        ValidateCatchUpTransition(state, errors);
        ValidateEvidenceClock(state, errors);
        if (state.PendingDelivery?.FinalizationPlan?.DeferredOccurrence is { } plannedDeferred
            && ScheduleIdentityDerivation.TryDerive(
                state.ScheduleId,
                state.DefinitionRevision,
                state.DefinitionHash,
                plannedDeferred.Occurrence,
                out var plannedDeferredIdentity,
                out _)
            && !Equals(plannedDeferredIdentity, plannedDeferred.Identity))
        {
            errors.Add(Error("deferred_identity_mismatch", "pendingDelivery.finalizationPlan.deferredOccurrence.identity"));
        }

        return Result(errors);
    }

    private static void ValidateFinalizationPlanAgainstOccurrence(
        ScheduleFinalizationPlan? plan,
        ScheduleOccurrence? current,
        string path,
        List<ScheduleContractError> errors)
    {
        if (plan is null || current is null)
        {
            return;
        }

        if (plan.NextOccurrence is { } next
            && (next.Ordinal <= current.Ordinal
                || next.ScheduledAtUtc <= current.ScheduledAtUtc
                || !Equals(next.TimeZone, current.TimeZone)))
        {
            errors.Add(Error("invalid_successor_occurrence", $"{path}.nextOccurrence"));
        }

        if (plan.DispositionEvidence is null)
        {
            return;
        }

        foreach (var evidence in plan.DispositionEvidence)
        {
            if (evidence is null)
            {
                continue;
            }

            var retainedOverlapDeferral = IsExactRetainedOverlapDeferral(plan, evidence);
            if (evidence.FirstOrdinal <= current.Ordinal
                || plan.NextOccurrence is not null
                    && evidence.LastOrdinal >= plan.NextOccurrence.Ordinal
                    && !retainedOverlapDeferral
                || !Equals(evidence.TimeZone, current.TimeZone))
            {
                errors.Add(Error("invalid_successor_evidence", $"{path}.dispositionEvidence"));
                break;
            }
        }

        if (plan.NextOccurrence is { } successor
            && successor.Ordinal > current.Ordinal + 1
            && !CoversOrdinalRange(plan.DispositionEvidence, current.Ordinal + 1, successor.Ordinal - 1))
        {
            errors.Add(Error("successor_gap_not_covered", $"{path}.dispositionEvidence"));
        }
    }

    private static void ValidateDispositionCollection(
        IReadOnlyList<ScheduleOccurrenceDispositionEvidence>? evidence,
        int maximum,
        string path,
        List<ScheduleContractError> errors)
    {
        if (evidence is null)
        {
            errors.Add(Error("required", path));
            return;
        }

        if (evidence.Count > maximum)
        {
            errors.Add(Error("evidence_limit_exceeded", path));
        }

        for (var index = 0; index < Math.Min(evidence.Count, maximum + 1); index++)
        {
            AddNested(errors, ValidateDispositionEvidence(evidence[index]), $"{path}[{index}]");
        }

        var seen = new HashSet<ScheduleOccurrenceDispositionEvidence>();
        for (var index = 0; index < Math.Min(evidence.Count, maximum + 1); index++)
        {
            var current = evidence[index];
            if (current is not null && !seen.Add(current))
            {
                errors.Add(Error("duplicate_evidence", path));
            }

            if (index == 0)
            {
                continue;
            }

            var previous = evidence[index - 1];
            if (ScheduleEvidenceOrdering.Compare(previous, current) > 0)
            {
                errors.Add(Error("noncanonical_evidence_order", path));
            }

            if (previous is not null
                && current is not null
                && previous.LastOrdinal >= current.FirstOrdinal
                && !IsRetainedDeferralSupersession(previous, current))
            {
                errors.Add(Error("overlapping_evidence", path));
            }
        }
    }

    private static bool IsRetainedDeferralSupersession(
        ScheduleOccurrenceDispositionEvidence previous,
        ScheduleOccurrenceDispositionEvidence current)
        => previous.Disposition == ScheduleOccurrenceDisposition.OverlapDeferred
            && current.Disposition == ScheduleOccurrenceDisposition.MisfireSkipped
            && previous.FirstOrdinal == previous.LastOrdinal
            && current.FirstOrdinal == current.LastOrdinal
            && previous.FirstOrdinal == current.FirstOrdinal
            && previous.Count == 1
            && current.Count == 1
            && previous.FirstScheduledLocal == current.FirstScheduledLocal
            && previous.LastScheduledLocal == current.LastScheduledLocal
            && previous.FirstScheduledAtUtc == current.FirstScheduledAtUtc
            && previous.LastScheduledAtUtc == current.LastScheduledAtUtc
            && Equals(previous.TimeZone, current.TimeZone)
            && current.RecordedAtUtc >= previous.RecordedAtUtc;

    private static void ValidateDeferredDisposition(ScheduleFinalizationPlan plan, List<ScheduleContractError> errors)
    {
        var deferredEvidenceCount = 0;
        if (plan.DispositionEvidence is not null)
        {
            for (var index = 0; index < plan.DispositionEvidence.Count; index++)
            {
                var evidence = plan.DispositionEvidence[index];
                if (evidence?.Disposition != ScheduleOccurrenceDisposition.OverlapDeferred)
                {
                    continue;
                }

                deferredEvidenceCount++;
                if (!IsExactRetainedOverlapDeferral(plan, evidence))
                {
                    errors.Add(Error("unretained_overlap_deferral", $"dispositionEvidence[{index}]"));
                }
            }
        }

        if (plan.DeferredOccurrence is not null && deferredEvidenceCount != 1)
        {
            errors.Add(Error("overlap_deferral_evidence_required", "deferredOccurrence"));
        }


        if (deferredEvidenceCount > 1)
        {
            errors.Add(Error("multiple_overlap_deferrals", "dispositionEvidence"));
        }
    }

    private static bool IsExactRetainedOverlapDeferral(
        ScheduleFinalizationPlan plan,
        ScheduleOccurrenceDispositionEvidence evidence)
    {
        var occurrence = plan.DeferredOccurrence?.Occurrence;
        return evidence.Disposition == ScheduleOccurrenceDisposition.OverlapDeferred
            && occurrence is not null
            && Equals(plan.NextOccurrence, occurrence)
            && evidence.FirstOrdinal == occurrence.Ordinal
            && evidence.LastOrdinal == occurrence.Ordinal
            && evidence.FirstScheduledLocal == occurrence.ScheduledLocal
            && evidence.LastScheduledLocal == occurrence.ScheduledLocal
            && evidence.FirstScheduledAtUtc == occurrence.ScheduledAtUtc
            && evidence.LastScheduledAtUtc == occurrence.ScheduledAtUtc
            && Equals(evidence.TimeZone, occurrence.TimeZone);
    }

    private static void ValidateRecurrenceSuccessor(
        ScheduleRecurrenceRule recurrence,
        ScheduleOccurrence current,
        ScheduleFinalizationPlan plan,
        List<ScheduleContractError> errors)
    {
        var next = plan.NextOccurrence;
        if (recurrence.Kind == ScheduleRecurrenceKind.Once)
        {
            if (next is not null)
            {
                errors.Add(Error("once_recurrence_not_exhausted", "state.pendingDelivery.finalizationPlan.nextOccurrence"));
            }

            return;
        }

        if (next is null)
        {
            if (CanAdvanceRecurrence(recurrence, current))
            {
                errors.Add(Error("recurrence_successor_required", "state.pendingDelivery.finalizationPlan.nextOccurrence"));
            }

            return;
        }

        if (next.Ordinal <= current.Ordinal)
        {
            return;
        }

        var ordinalDelta = next.Ordinal - current.Ordinal;
        var valid = recurrence.Kind switch
        {
            ScheduleRecurrenceKind.FixedInterval when recurrence.FixedIntervalSeconds is { } interval
                => (decimal)(next.ScheduledAtUtc.UtcDateTime.Ticks - current.ScheduledAtUtc.UtcDateTime.Ticks)
                    == (decimal)ordinalDelta * interval * TimeSpan.TicksPerSecond,
            ScheduleRecurrenceKind.Daily
                => (decimal)(next.ScheduledLocal.Ticks - current.ScheduledLocal.Ticks)
                    == (decimal)ordinalDelta * TimeSpan.TicksPerDay,
            ScheduleRecurrenceKind.Weekly
                => (decimal)(next.ScheduledLocal.Ticks - current.ScheduledLocal.Ticks)
                    == (decimal)ordinalDelta * 7 * TimeSpan.TicksPerDay,
            _ => false,
        };
        if (!valid)
        {
            errors.Add(Error("recurrence_successor_mismatch", "state.pendingDelivery.finalizationPlan.nextOccurrence"));
        }
    }

    private static bool CanAdvanceRecurrence(
        ScheduleRecurrenceRule recurrence,
        ScheduleOccurrence current)
    {
        if (current.Ordinal >= ScheduleContractLimits.MaxOccurrenceOrdinal)
        {
            return false;
        }

        decimal requiredTicks = recurrence.Kind switch
        {
            ScheduleRecurrenceKind.FixedInterval when recurrence.FixedIntervalSeconds is { } seconds
                => (decimal)seconds * TimeSpan.TicksPerSecond,
            ScheduleRecurrenceKind.Daily => TimeSpan.TicksPerDay,
            ScheduleRecurrenceKind.Weekly => 7m * TimeSpan.TicksPerDay,
            _ => 0,
        };
        if (requiredTicks <= 0)
        {
            return false;
        }

        var maximumSupportedTicks = new DateTime(
            ScheduleContractLimits.MaximumSupportedYear + 1,
            1,
            1,
            0,
            0,
            0,
            DateTimeKind.Unspecified).Ticks - 1m;

        return recurrence.Kind == ScheduleRecurrenceKind.FixedInterval
            ? (decimal)current.ScheduledAtUtc.UtcDateTime.Ticks + requiredTicks <= maximumSupportedTicks
            : (decimal)current.ScheduledLocal.Ticks + requiredTicks <= maximumSupportedTicks;
    }

    private static void ValidateDefinitionBoundState(
        ScheduleDefinition definition,
        ScheduleState state,
        List<ScheduleContractError> errors)
    {
        ValidateDefinitionBoundOccurrence(definition, state.NextOccurrence, "state.nextOccurrence", errors);
        ValidateDefinitionBoundCatchUp(definition, state.CatchUpEpisode, "state.catchUpEpisode", errors);
        ValidateDefinitionBoundDeferred(definition, state.DeferredOccurrence, "state.deferredOccurrence", errors);

        if (state.PendingDelivery is { } pending)
        {
            ValidateDefinitionBoundOccurrence(definition, pending.Occurrence, "state.pendingDelivery.occurrence", errors);
            if (pending.FinalizationPlan is { } plan)
            {
                ValidateDefinitionBoundOccurrence(
                    definition,
                    plan.NextOccurrence,
                    "state.pendingDelivery.finalizationPlan.nextOccurrence",
                    errors);
                ValidateDefinitionBoundCatchUp(
                    definition,
                    plan.CatchUpEpisode,
                    "state.pendingDelivery.finalizationPlan.catchUpEpisode",
                    errors);
                ValidateDefinitionBoundDeferred(
                    definition,
                    plan.DeferredOccurrence,
                    "state.pendingDelivery.finalizationPlan.deferredOccurrence",
                    errors);
                ValidateDefinitionBoundDispositionCollection(
                    definition,
                    plan.DispositionEvidence,
                    "state.pendingDelivery.finalizationPlan.dispositionEvidence",
                    errors);
            }
        }

        ValidateDefinitionBoundDispositionCollection(
            definition,
            state.DispositionEvidence,
            "state.dispositionEvidence",
            errors);

        for (var index = 0; index < state.TerminalDeliveryEvidence.Count; index++)
        {
            ValidateDefinitionBoundOccurrence(
                definition,
                state.TerminalDeliveryEvidence[index].Occurrence,
                $"state.terminalDeliveryEvidence[{index}].occurrence",
                errors);
        }
    }

    private static void ValidateDefinitionBoundOccurrence(
        ScheduleDefinition definition,
        ScheduleOccurrence? occurrence,
        string path,
        List<ScheduleContractError> errors)
    {
        if (occurrence is null)
        {
            return;
        }

        if (!Equals(occurrence.TimeZone, definition.TimeZone))
        {
            errors.Add(Error("definition_time_zone_mismatch", $"{path}.timeZone"));
        }

        if (!MatchesRecurrenceAnchor(
                definition.Recurrence,
                occurrence.Ordinal,
                occurrence.ScheduledLocal))
        {
            errors.Add(Error("recurrence_anchor_mismatch", $"{path}.scheduledLocal"));
        }
    }

    private static bool MatchesRecurrenceAnchor(
        ScheduleRecurrenceRule recurrence,
        long ordinal,
        DateTime scheduledLocal)
    {
        if (recurrence.Kind == ScheduleRecurrenceKind.Once)
        {
            return ordinal == 1 && scheduledLocal == recurrence.FirstLocalOccurrence;
        }

        if (recurrence.Kind == ScheduleRecurrenceKind.FixedInterval)
        {
            return ordinal != 1 || scheduledLocal == recurrence.FirstLocalOccurrence;
        }

        decimal periodTicks = recurrence.Kind switch
        {
            ScheduleRecurrenceKind.Daily => TimeSpan.TicksPerDay,
            ScheduleRecurrenceKind.Weekly => 7m * TimeSpan.TicksPerDay,
            _ => 0,
        };
        if (periodTicks == 0)
        {
            return false;
        }

        var expectedTicks = (decimal)recurrence.FirstLocalOccurrence.Ticks
            + (ordinal - 1m) * periodTicks;
        return expectedTicks >= DateTime.MinValue.Ticks
            && expectedTicks <= DateTime.MaxValue.Ticks
            && scheduledLocal.Ticks == (long)expectedTicks;
    }

    private static void ValidateDefinitionBoundCatchUp(
        ScheduleDefinition definition,
        ScheduleCatchUpEpisode? episode,
        string path,
        List<ScheduleContractError> errors)
    {
        if (episode is null)
        {
            return;
        }

        if (definition.Misfire.Kind != ScheduleMisfirePolicyKind.CatchUp
            || episode.RemainingAdmittedOccurrences > definition.Misfire.CatchUpLimit)
        {
            errors.Add(Error("catch_up_policy_mismatch", path));
        }
    }

    private static void ValidateDefinitionBoundDeferred(
        ScheduleDefinition definition,
        ScheduleDeferredOccurrence? deferred,
        string path,
        List<ScheduleContractError> errors)
    {
        if (deferred is null)
        {
            return;
        }

        ValidateDefinitionBoundOccurrence(definition, deferred.Occurrence, $"{path}.occurrence", errors);
        if (definition.Overlap != ScheduleOverlapPolicy.DeferOne)
        {
            errors.Add(Error("overlap_policy_mismatch", path));
        }
    }

    private static void ValidateDefinitionBoundDispositionCollection(
        ScheduleDefinition definition,
        IReadOnlyList<ScheduleOccurrenceDispositionEvidence> evidence,
        string path,
        List<ScheduleContractError> errors)
    {
        for (var index = 0; index < evidence.Count; index++)
        {
            var item = evidence[index];
            if (!Equals(item.TimeZone, definition.TimeZone))
            {
                errors.Add(Error("definition_time_zone_mismatch", $"{path}[{index}].timeZone"));
            }

            if (!MatchesRecurrenceAnchor(
                    definition.Recurrence,
                    item.FirstOrdinal,
                    item.FirstScheduledLocal))
            {
                errors.Add(Error("recurrence_anchor_mismatch", $"{path}[{index}].firstScheduledLocal"));
            }

            if (!MatchesRecurrenceAnchor(
                    definition.Recurrence,
                    item.LastOrdinal,
                    item.LastScheduledLocal))
            {
                errors.Add(Error("recurrence_anchor_mismatch", $"{path}[{index}].lastScheduledLocal"));
            }

            var policyMatches = item.Disposition switch
            {
                ScheduleOccurrenceDisposition.InvalidLocalTimeSkipped
                    => definition.DaylightSaving.InvalidLocalTime == ScheduleInvalidLocalTimePolicy.Skip,
                ScheduleOccurrenceDisposition.OverlapSkipped
                    => definition.Overlap == ScheduleOverlapPolicy.Skip,
                ScheduleOccurrenceDisposition.OverlapDeferred
                    => definition.Overlap == ScheduleOverlapPolicy.DeferOne,
                _ => true,
            };
            if (!policyMatches)
            {
                errors.Add(Error("disposition_policy_mismatch", $"{path}[{index}].disposition"));
            }
        }
    }

    private static void ValidateStateDispositionCoordinates(ScheduleState state, List<ScheduleContractError> errors)
    {
        if (state.DispositionEvidence is null)
        {
            return;
        }

        var activeDeferredEvidence = 0;
        for (var index = 0; index < state.DispositionEvidence.Count; index++)
        {
            var evidence = state.DispositionEvidence[index];
            if (evidence is null)
            {
                continue;
            }

            if (evidence.Disposition != ScheduleOccurrenceDisposition.OverlapDeferred)
            {
                if (state.NextOccurrence is { } next && evidence.LastOrdinal >= next.Ordinal)
                {
                    errors.Add(Error("final_disposition_not_predecessor", $"dispositionEvidence[{index}]"));
                }

                if (state.PendingDelivery?.Occurrence is { } pendingOccurrence
                    && evidence.FirstOrdinal <= pendingOccurrence.Ordinal
                    && evidence.LastOrdinal >= pendingOccurrence.Ordinal)
                {
                    errors.Add(Error("final_disposition_covers_pending", $"dispositionEvidence[{index}]"));
                }

                continue;
            }

            if (state.NextOccurrence is not { } nextOccurrence
                || evidence.LastOrdinal < nextOccurrence.Ordinal)
            {
                continue;
            }

            if (evidence.FirstOrdinal > nextOccurrence.Ordinal
                || evidence.LastOrdinal > nextOccurrence.Ordinal)
            {
                errors.Add(Error("overlap_deferral_not_reachable", $"dispositionEvidence[{index}]"));
                continue;
            }

            activeDeferredEvidence++;
            if (state.DeferredOccurrence?.Occurrence is not { } deferredOccurrence
                || !Equals(deferredOccurrence, nextOccurrence)
                || evidence.FirstScheduledLocal != deferredOccurrence.ScheduledLocal
                || evidence.LastScheduledLocal != deferredOccurrence.ScheduledLocal
                || evidence.FirstScheduledAtUtc != deferredOccurrence.ScheduledAtUtc
                || evidence.LastScheduledAtUtc != deferredOccurrence.ScheduledAtUtc
                || !Equals(evidence.TimeZone, deferredOccurrence.TimeZone))
            {
                errors.Add(Error("unretained_state_overlap_deferral", $"dispositionEvidence[{index}]"));
            }
        }

        if (activeDeferredEvidence > 1)
        {
            errors.Add(Error("multiple_active_overlap_deferrals", "dispositionEvidence"));
        }

        if (state.DeferredOccurrence is not null && activeDeferredEvidence != 1)
        {
            errors.Add(Error("active_overlap_deferral_evidence_required", "deferredOccurrence"));
        }
    }

    private static void ValidateEvidenceClock(ScheduleState state, List<ScheduleContractError> errors)
    {
        var hasBoundEvidence = state.PendingDelivery is not null
            || state.DeferredOccurrence is not null
            || state.DispositionEvidence?.Count > 0
            || state.TerminalDeliveryEvidence?.Count > 0
            || state.PendingDelivery?.FinalizationPlan?.DeferredOccurrence is not null
            || state.PendingDelivery?.FinalizationPlan?.DispositionEvidence?.Count > 0;
        if (!hasBoundEvidence)
        {
            return;
        }

        if (state.LastClockObservedAtUtc is not { } clock || !IsUtc(clock))
        {
            errors.Add(Error("evidence_clock_required", "lastClockObservedAtUtc"));
            return;
        }

        static void Check(
            DateTimeOffset timestamp,
            string path,
            DateTimeOffset clock,
            List<ScheduleContractError> errors)
        {
            if (IsUtc(timestamp) && timestamp > clock)
            {
                errors.Add(Error("evidence_after_clock", path));
            }
        }

        if (state.PendingDelivery is { } pending)
        {
            Check(pending.ClaimedAtUtc, "pendingDelivery.claimedAtUtc", clock, errors);
            if (pending.Prepared is { } prepared)
            {
                Check(prepared.PreparedAtUtc, "pendingDelivery.prepared.preparedAtUtc", clock, errors);
            }

            if (pending.Result is { } result)
            {
                Check(result.RecordedAtUtc, "pendingDelivery.result.recordedAtUtc", clock, errors);
            }

            if (pending.FinalizationPlan?.DeferredOccurrence is { } plannedDeferred)
            {
                Check(plannedDeferred.DeferredAtUtc, "pendingDelivery.finalizationPlan.deferredOccurrence.deferredAtUtc", clock, errors);
            }

            if (pending.FinalizationPlan?.DispositionEvidence is { } plannedDispositions)
            {
                for (var index = 0; index < plannedDispositions.Count; index++)
                {
                    if (plannedDispositions[index] is { } evidence)
                    {
                        Check(evidence.RecordedAtUtc, $"pendingDelivery.finalizationPlan.dispositionEvidence[{index}].recordedAtUtc", clock, errors);
                    }
                }
            }
        }

        if (state.DeferredOccurrence is { } deferred)
        {
            Check(deferred.DeferredAtUtc, "deferredOccurrence.deferredAtUtc", clock, errors);
        }

        if (state.DispositionEvidence is { } dispositions)
        {
            for (var index = 0; index < dispositions.Count; index++)
            {
                if (dispositions[index] is { } evidence)
                {
                    Check(evidence.RecordedAtUtc, $"dispositionEvidence[{index}].recordedAtUtc", clock, errors);
                }
            }
        }

        if (state.TerminalDeliveryEvidence is { } terminalEvidence)
        {
            for (var index = 0; index < terminalEvidence.Count; index++)
            {
                var terminal = terminalEvidence[index];
                if (terminal?.Result is not null)
                {
                    Check(terminal.Result.RecordedAtUtc, $"terminalDeliveryEvidence[{index}].result.recordedAtUtc", clock, errors);
                }

                if (terminal is not null)
                {
                    Check(terminal.FinalizedAtUtc, $"terminalDeliveryEvidence[{index}].finalizedAtUtc", clock, errors);
                }
            }
        }
    }

    private static void ValidateCatchUpEpisode(
        ScheduleCatchUpEpisode? episode,
        ScheduleOccurrence? nextOccurrence,
        string path,
        List<ScheduleContractError> errors)
    {
        if (episode is null)
        {
            return;
        }

        ValidateSchema(episode.SchemaVersion, $"{path}.schemaVersion", errors);
        ValidateOrdinal(episode.LatestDueOrdinal, $"{path}.latestDueOrdinal", errors);
        if (episode.RemainingAdmittedOccurrences is < 1 or > ScheduleContractLimits.MaxCatchUpOccurrences)
        {
            errors.Add(Error("catch_up_remaining_out_of_range", $"{path}.remainingAdmittedOccurrences"));
        }

        if (nextOccurrence is null
            || nextOccurrence.Ordinal > episode.LatestDueOrdinal
            || episode.RemainingAdmittedOccurrences > episode.LatestDueOrdinal - nextOccurrence.Ordinal + 1)
        {
            errors.Add(Error("invalid_catch_up_episode", path));
        }
    }

    private static void ValidateDeferredOccurrence(
        ScheduleDeferredOccurrence? deferred,
        string path,
        List<ScheduleContractError> errors)
    {
        if (deferred is null)
        {
            return;
        }

        ValidateSchema(deferred.SchemaVersion, $"{path}.schemaVersion", errors);
        AddNested(errors, ValidateOccurrence(deferred.Occurrence), $"{path}.occurrence");
        ValidateIdentityShape(deferred.Identity, $"{path}.identity", errors);
        ValidateUtc(deferred.DeferredAtUtc, $"{path}.deferredAtUtc", errors);
        if (deferred.Occurrence is not null
            && IsUtc(deferred.DeferredAtUtc)
            && deferred.DeferredAtUtc < deferred.Occurrence.ScheduledAtUtc)
        {
            errors.Add(Error("deferred_before_occurrence", $"{path}.deferredAtUtc"));
        }
    }

    private static void ValidateCatchUpTransition(ScheduleState state, List<ScheduleContractError> errors)
    {
        if (state.PendingDelivery?.FinalizationPlan is not { } plan
            || state.PendingDelivery.Occurrence is not { } pendingOccurrence)
        {
            return;
        }

        var current = state.CatchUpEpisode;
        var successor = plan.CatchUpEpisode;
        if (current is null)
        {
            if (successor is not null)
            {
                errors.Add(Error("unexpected_catch_up_successor", "pendingDelivery.finalizationPlan.catchUpEpisode"));
            }

            return;
        }

        var remaining = current.RemainingAdmittedOccurrences - 1;
        if (remaining > 0)
        {
            if (successor is null
                || successor.LatestDueOrdinal != current.LatestDueOrdinal
                || successor.RemainingAdmittedOccurrences != remaining)
            {
                errors.Add(Error("invalid_catch_up_successor", "pendingDelivery.finalizationPlan.catchUpEpisode"));
            }

            return;
        }

        if (successor is not null)
        {
            errors.Add(Error("catch_up_budget_renewed", "pendingDelivery.finalizationPlan.catchUpEpisode"));
        }

        var firstSkipped = pendingOccurrence.Ordinal + 1;
        if (plan.NextOccurrence is not null && plan.NextOccurrence.Ordinal <= current.LatestDueOrdinal)
        {
            errors.Add(Error("catch_up_episode_not_exhausted", "pendingDelivery.finalizationPlan.nextOccurrence"));
        }

        if (firstSkipped <= current.LatestDueOrdinal
            && !CoversOrdinalRange(plan.DispositionEvidence, firstSkipped, current.LatestDueOrdinal))
        {
            errors.Add(Error("catch_up_skip_range_incomplete", "pendingDelivery.finalizationPlan.dispositionEvidence"));
        }

        if (plan.DispositionEvidence is not null)
        {
            for (var index = 0; index < plan.DispositionEvidence.Count; index++)
            {
                var item = plan.DispositionEvidence[index];
                if (item is not null
                    && item.Disposition != ScheduleOccurrenceDisposition.InvalidLocalTimeSkipped
                    && item.LastOrdinal > current.LatestDueOrdinal)
                {
                    errors.Add(Error("catch_up_skip_range_exceeded", "pendingDelivery.finalizationPlan.dispositionEvidence"));
                    break;
                }
            }
        }
    }

    private static bool CoversOrdinalRange(
        IReadOnlyList<ScheduleOccurrenceDispositionEvidence>? evidence,
        long first,
        long last)
    {
        if (evidence is null)
        {
            return false;
        }

        var expected = first;
        for (var index = 0; index < evidence.Count; index++)
        {
            var item = evidence[index];
            if (item is null
                || item.Disposition == ScheduleOccurrenceDisposition.OverlapDeferred
                || item.FirstOrdinal < first
                || item.LastOrdinal > last)
            {
                continue;
            }

            if (item.FirstOrdinal != expected)
            {
                return false;
            }

            expected = item.LastOrdinal + 1;
        }

        return expected == last + 1;
    }

    private static void ValidateTerminalCollection(ScheduleState state, List<ScheduleContractError> errors)
    {
        var evidence = state.TerminalDeliveryEvidence;
        const string Path = "terminalDeliveryEvidence";
        if (evidence is null)
        {
            errors.Add(Error("required", Path));
            return;
        }

        if (evidence.Count > ScheduleContractLimits.MaxTerminalDeliveryEvidenceItems)
        {
            errors.Add(Error("evidence_limit_exceeded", Path));
        }

        var ordinals = new HashSet<long>();
        var identities = new HashSet<ScheduleOccurrenceId>();

        for (var index = 0; index < Math.Min(evidence.Count, ScheduleContractLimits.MaxTerminalDeliveryEvidenceItems + 1); index++)
        {
            var item = evidence[index];
            AddNested(errors, ValidateTerminalDeliveryEvidence(item), $"{Path}[{index}]");
            if (item is null)
            {
                continue;
            }

            if (ScheduleIdentityDerivation.TryDerive(
                    state.ScheduleId,
                    state.DefinitionRevision,
                    state.DefinitionHash,
                    item.Occurrence,
                    out var expected,
                    out _)
                && !Equals(expected, item.Identity))
            {
                errors.Add(Error("terminal_identity_mismatch", $"{Path}[{index}].identity"));
            }

            if (item.Occurrence is not null && !ordinals.Add(item.Occurrence.Ordinal)
                || item.Identity?.OccurrenceId is not null && !identities.Add(item.Identity.OccurrenceId))
            {
                errors.Add(Error("duplicate_terminal_identity", $"{Path}[{index}].identity"));
            }

            if (state.PendingDelivery is not null
                && (Equals(state.PendingDelivery.Identity, item.Identity)
                    || item.Occurrence is not null
                        && state.PendingDelivery.Occurrence is { } pendingOccurrence
                        && pendingOccurrence.Ordinal == item.Occurrence.Ordinal))
            {
                errors.Add(Error("pending_delivery_already_terminal", $"{Path}[{index}].identity"));
            }

            if (state.NextOccurrence is not null
                && item.Occurrence is not null
                && item.Occurrence.Ordinal >= state.NextOccurrence.Ordinal)
            {
                errors.Add(Error("terminal_occurrence_not_predecessor", $"{Path}[{index}].occurrence"));
            }

            if (item.Occurrence is not null
                && (ContainsFinalDispositionOrdinal(state.DispositionEvidence, item.Occurrence.Ordinal)
                    || ContainsFinalDispositionOrdinal(state.PendingDelivery?.FinalizationPlan?.DispositionEvidence, item.Occurrence.Ordinal)))
            {
                errors.Add(Error("terminal_occurrence_already_disposed", $"{Path}[{index}].occurrence"));
            }
        }

        for (var index = 1; index < Math.Min(evidence.Count, ScheduleContractLimits.MaxTerminalDeliveryEvidenceItems + 1); index++)
        {
            if (ScheduleTerminalEvidenceOrdering.Compare(evidence[index - 1], evidence[index]) > 0)
            {
                errors.Add(Error("noncanonical_evidence_order", Path));
                break;
            }
        }
    }

    private static bool ContainsFinalDispositionOrdinal(
        IReadOnlyList<ScheduleOccurrenceDispositionEvidence>? evidence,
        long ordinal)
    {
        if (evidence is null)
        {
            return false;
        }

        for (var index = 0; index < evidence.Count; index++)
        {
            var item = evidence[index];
            if (item is not null
                && item.Disposition != ScheduleOccurrenceDisposition.OverlapDeferred
                && item.FirstOrdinal <= ordinal
                && item.LastOrdinal >= ordinal)
            {
                return true;
            }
        }

        return false;
    }

    internal static ScheduleContractValidationResult ValidateIdentityCoordinates(
        ScheduleId? scheduleId,
        long definitionRevision,
        string? definitionHash,
        ScheduleOccurrence? occurrence)
    {
        var errors = new List<ScheduleContractError>();
        ValidateScheduleId(scheduleId, "scheduleId", errors);
        ValidateRevision(definitionRevision, "definitionRevision", errors);
        if (!IsSha256(definitionHash))
        {
            errors.Add(Error("invalid_hash", "definitionHash"));
        }

        AddNested(errors, ValidateOccurrence(occurrence), "occurrence");
        return Result(errors);
    }

    private static IEnumerable<ScheduleContractError> ValidateRecurrence(ScheduleRecurrenceRule? recurrence, string path)
    {
        if (recurrence is null)
        {
            return [Error("required", path)];
        }

        var errors = new List<ScheduleContractError>();
        if (!IsDefined(recurrence.Kind))
        {
            errors.Add(Error("unsupported_recurrence", $"{path}.kind"));
        }

        ValidateLocal(recurrence.FirstLocalOccurrence, $"{path}.firstLocalOccurrence", errors);
        if (recurrence.Kind == ScheduleRecurrenceKind.FixedInterval)
        {
            if (recurrence.FixedIntervalSeconds is not (>= 1 and <= ScheduleContractLimits.MaxFixedIntervalSeconds))
            {
                errors.Add(Error("invalid_fixed_interval", $"{path}.fixedIntervalSeconds"));
            }
        }
        else if (recurrence.FixedIntervalSeconds is not null)
        {
            errors.Add(Error("unexpected_fixed_interval", $"{path}.fixedIntervalSeconds"));
        }

        return errors;
    }

    private static IEnumerable<ScheduleContractError> ValidateMisfire(ScheduleMisfirePolicy? misfire, string path)
    {
        if (misfire is null)
        {
            return [Error("required", path)];
        }

        var errors = new List<ScheduleContractError>();
        if (!IsDefined(misfire.Kind))
        {
            errors.Add(Error("unsupported_misfire_policy", $"{path}.kind"));
        }

        if (misfire.Kind == ScheduleMisfirePolicyKind.CatchUp)
        {
            if (misfire.CatchUpLimit is < 1 or > ScheduleContractLimits.MaxCatchUpOccurrences)
            {
                errors.Add(Error("invalid_catch_up_limit", $"{path}.catchUpLimit"));
            }
        }
        else if (misfire.CatchUpLimit != 0)
        {
            errors.Add(Error("unexpected_catch_up_limit", $"{path}.catchUpLimit"));
        }

        return errors;
    }

    private static IEnumerable<ScheduleContractError> ValidateTimeZone(ScheduleTimeZoneReference? timeZone, string path)
    {
        if (timeZone is null)
        {
            return [Error("required", path)];
        }

        var errors = new List<ScheduleContractError>();
        if (!IsTimeZoneId(timeZone.TimeZoneId))
        {
            errors.Add(Error("invalid_time_zone_id", $"{path}.timeZoneId"));
        }

        if (!IsSha256(timeZone.RulesFingerprint))
        {
            errors.Add(Error("invalid_hash", $"{path}.rulesFingerprint"));
        }

        return errors;
    }

    private static void ValidateAuthorityProfile(Authority.Models.AuthorityProfileReference? profile, string path, List<ScheduleContractError> errors)
    {
        if (profile?.ProfileId is null
            || profile.Revision is null
            || !AuthorityProfileId.TryParse(profile.ProfileId.Value, out _, out _)
            || !AuthorityProfileRevision.TryParse(profile.Revision.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), out _, out _))
        {
            errors.Add(Error("invalid_authority_profile", path));
        }
    }

    private static void ValidatePayload(SchedulePayloadReference? payload, string path, List<ScheduleContractError> errors)
    {
        if (payload?.ContentHash is null
            || !TriggerDeliveryFactory.TryCreateReferencedPayload(payload.GovernedReference, payload.ContentHash, out _, out _))
        {
            errors.Add(Error("invalid_payload_reference", path));
        }
    }

    private static void ValidateIdentityShape(ScheduleOccurrenceIdentity? identity, string path, List<ScheduleContractError> errors)
    {
        if (identity?.OccurrenceId is null
            || identity.DeliveryId is null
            || identity.DeduplicationId is null
            || !ScheduleOccurrenceId.TryParse(identity.OccurrenceId.Value, out _)
            || !TriggerDeliveryId.TryParse(identity.DeliveryId.Value, out _)
            || !TriggerDeduplicationId.TryParse(identity.DeduplicationId.Value, out _))
        {
            errors.Add(Error("invalid_occurrence_identity", path));
        }
    }

    private static void ValidateScheduleId(ScheduleId? scheduleId, string path, List<ScheduleContractError> errors)
    {
        if (scheduleId is null || !ScheduleId.TryParse(scheduleId.Value, out _))
        {
            errors.Add(Error("invalid_schedule_id", path));
        }
    }

    private static void ValidateArtifactId(string? value, int maximum, string path, List<ScheduleContractError> errors)
    {
        if (!CustomLoopArtifactIdentifier.IsValid(value, maximum))
        {
            errors.Add(Error("invalid_identifier", path));
        }
    }

    private static void ValidateRevision(long value, string path, List<ScheduleContractError> errors)
    {
        if (!IsRevision(value))
        {
            errors.Add(Error("revision_out_of_range", path));
        }
    }

    private static void ValidateOrdinal(long value, string path, List<ScheduleContractError> errors)
    {
        if (value is < 1 or > ScheduleContractLimits.MaxOccurrenceOrdinal)
        {
            errors.Add(Error("ordinal_out_of_range", path));
        }
    }

    private static void ValidateSchema(int value, string path, List<ScheduleContractError> errors)
    {
        if (value != ScheduleContractLimits.CurrentSchemaVersion)
        {
            errors.Add(Error("unsupported_schema_version", path));
        }
    }

    private static void ValidateLocal(DateTime value, string path, List<ScheduleContractError> errors)
    {
        if (value.Kind != DateTimeKind.Unspecified || !IsSupportedYear(value.Year))
        {
            errors.Add(Error("invalid_local_time", path));
        }
    }

    private static void ValidateUtc(DateTimeOffset value, string path, List<ScheduleContractError> errors)
    {
        if (!IsUtc(value))
        {
            errors.Add(Error("utc_required", path));
        }
    }

    private static void ValidateReason(string? value, string path, List<ScheduleContractError> errors)
    {
        if (!CustomLoopArtifactIdentifier.IsValid(value, ScheduleContractLimits.MaxReasonCodeCharacters))
        {
            errors.Add(Error("invalid_reason_code", path));
        }
    }

    private static void AddNested(List<ScheduleContractError> target, ScheduleContractValidationResult nested, string prefix)
    {
        foreach (var error in nested.Errors)
        {
            target.Add(error with { Path = error.Path == "$" ? prefix : $"{prefix}.{error.Path}" });
        }
    }

    private static bool IsDefined<TEnum>(TEnum value) where TEnum : struct, Enum
        => Enum.IsDefined(value) && Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture) != 0;

    private static bool IsRevision(long value) => value is >= 1 and <= ScheduleContractLimits.MaxRevision;

    private static bool IsTerminal(ScheduleDeliveryResultKind value)
        => value is ScheduleDeliveryResultKind.Queued
            or ScheduleDeliveryResultKind.Replayed
            or ScheduleDeliveryResultKind.Rejected;

    private static bool IsSha256(string? value)
        => value?.Length == ScheduleContractLimits.Sha256HexCharacters
            && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsUtc(DateTimeOffset value)
        => value.Offset == TimeSpan.Zero && IsSupportedYear(value.UtcDateTime.Year);

    private static bool IsSupportedYear(int year)
        => year is >= ScheduleContractLimits.MinimumSupportedYear and <= ScheduleContractLimits.MaximumSupportedYear;

    private static bool IsTimeZoneId(string? value)
    {
        if (!TriggerTextRules.IsSafeNormalized(value, ScheduleContractLimits.MaxTimeZoneIdCharacters)
            || char.IsWhiteSpace(value![0])
            || char.IsWhiteSpace(value[^1])
            || value.Contains('\\', StringComparison.Ordinal))
        {
            return false;
        }

        return value.Split('/').All(segment => segment.Length > 0 && segment is not "." and not "..");
    }

    private static bool IsStrictUtf8(byte[] value)
    {
        try
        {
            _ = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetCharCount(value);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static ScheduleContractValidationResult Result(params ScheduleContractError[] errors) => new(errors);

    private static ScheduleContractValidationResult Result(IEnumerable<ScheduleContractError> errors) => new(errors);

    private static ScheduleContractError Error(string code, string path) => new(code, path);
}
