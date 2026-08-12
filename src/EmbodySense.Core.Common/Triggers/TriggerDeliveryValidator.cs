using System.Globalization;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Triggers.Models;
using EmbodySense.Core.Common.Triggers.Schedules;

namespace EmbodySense.Core.Common.Triggers;

/// <summary>
/// Revalidates trigger-delivery evidence at public boundaries.
/// </summary>
public static class TriggerDeliveryValidator
{
    /// <summary>
    /// Validates an envelope without reading current time or granting authority.
    /// </summary>
    /// <param name="envelope">The candidate envelope.</param>
    /// <returns>The structured validation result.</returns>
    public static TriggerContractValidationResult Validate(TriggerDeliveryEnvelope? envelope)
    {
        var errors = new List<TriggerContractError>();
        if (envelope is null)
        {
            return Result(Error("required", "$"));
        }

        if (envelope.SchemaVersion != TriggerDeliveryEnvelope.CurrentSchemaVersion)
        {
            errors.Add(Error("unsupported_schema_version", "schemaVersion"));
        }

        if (!Enum.IsDefined(envelope.Kind) || envelope.Kind == TriggerKind.Unknown)
        {
            errors.Add(Error("unsupported_trigger_kind", "kind"));
        }

        errors.AddRange(ValidateLoopReference(envelope.Loop).Errors);
        errors.AddRange(ValidateAdapterReference(envelope.Adapter).Errors);
        errors.AddRange(ValidateAuthorityEvidence(envelope.Authority).Errors);
        ValidateScheduleExecutionDirective(envelope, errors);
        if (envelope.Redelivery.Attempt == 1 && !envelope.Redelivery.OriginalDeliveryId.Equals(envelope.DeliveryId))
        {
            errors.Add(Error("invalid_original_delivery", "redelivery.originalDeliveryId"));
        }

        errors.AddRange(ValidateConversation(envelope.PublicationRequested, envelope.InvokingConversation, envelope.Temporal));
        if (!IsStatusReasonPair(envelope.VisibleStatus, envelope.VisibleReason))
        {
            errors.Add(Error("invalid_visible_outcome", "visibleStatus"));
        }

        var admissionVisible = envelope.VisibleStatus is TriggerAdmissionStatus.Admitted or TriggerAdmissionStatus.Replayed;
        if (admissionVisible != (envelope.Temporal?.AdmittedAtUtc is not null))
        {
            errors.Add(Error("invalid_admitted_time", "temporal.admittedAtUtc"));
        }

        return new TriggerContractValidationResult(errors);
    }

    private static void ValidateScheduleExecutionDirective(
        TriggerDeliveryEnvelope envelope,
        List<TriggerContractError> errors)
    {
        var directive = envelope.ScheduleExecutionDirective;
        if (envelope.Kind == TriggerKind.Time)
        {
            if (directive is null)
            {
                errors.Add(Error("schedule_execution_directive_required", "scheduleExecutionDirective"));
                return;
            }

            var validation = ScheduleContractValidator.ValidateExecutionDirective(directive);
            foreach (var error in validation.Errors)
            {
                errors.Add(Error(
                    error.Code,
                    error.Path == "$"
                        ? "scheduleExecutionDirective"
                        : $"scheduleExecutionDirective.{error.Path}"));
            }

            if (!Equals(directive.Target, envelope.Loop))
            {
                errors.Add(Error("schedule_target_mismatch", "scheduleExecutionDirective.target"));
            }

            if (directive.Identity is null
                || !Equals(directive.Identity.DeliveryId, envelope.DeliveryId)
                || !Equals(directive.Identity.DeduplicationId, envelope.DeduplicationId))
            {
                errors.Add(Error("schedule_identity_mismatch", "scheduleExecutionDirective.identity"));
            }

            if (directive.Occurrence is null
                || envelope.Temporal is null
                || directive.Occurrence.ScheduledAtUtc != envelope.Temporal.CreatedAtUtc)
            {
                errors.Add(Error("schedule_occurrence_mismatch", "scheduleExecutionDirective.occurrence"));
            }

            return;
        }

        if (directive is not null)
        {
            errors.Add(Error("schedule_execution_directive_forbidden", "scheduleExecutionDirective"));
        }
    }

    /// <summary>
    /// Validates that exactly one closed legacy-definition or governed-publication target arm is populated.
    /// </summary>
    /// <param name="loop">The candidate target reference.</param>
    /// <returns>The structured validation result.</returns>
    public static TriggerContractValidationResult ValidateLoopReference(TriggerLoopReference? loop)
    {
        if (loop is null || !Enum.IsDefined(loop.Kind) || loop.Kind == TriggerLoopTargetKind.Unknown)
        {
            return Result(Error("invalid_loop_reference", "loop"));
        }

        var valid = loop.Kind switch
        {
            TriggerLoopTargetKind.LegacyDefinition => loop.LegacyDefinition is { } legacy
                && loop.GovernedPublication is null
                && loop.AuthorityGrant is null
                && CustomLoopArtifactIdentifier.IsValid(legacy.LoopId, TriggerDeliveryLimits.MaxLoopIdCharacters)
                && legacy.DefinitionVersion is >= 1 and <= TriggerDeliveryLimits.MaxLoopDefinitionVersion
                && TriggerTextRules.IsSha256(legacy.ContentHash),
            TriggerLoopTargetKind.GovernedPublication => loop.LegacyDefinition is null
                && GovernedLoopRevisionContractValidator.Validate(loop.GovernedPublication).IsValid
                && loop.AuthorityGrant?.GrantId is not null
                && loop.AuthorityGrant.Revision is not null
                && AuthorityGrantId.TryParse(loop.AuthorityGrant.GrantId.Value, out _, out _)
                && AuthorityGrantRevision.TryParse(loop.AuthorityGrant.Revision.Value.ToString(CultureInfo.InvariantCulture), out _, out _)
                && AuthorityGrantHash.IsCanonical(loop.AuthorityGrant.ContentHash),
            _ => false
        };
        return valid ? Result() : Result(Error("invalid_loop_reference", "loop"));
    }

    internal static IReadOnlyList<TriggerContractError> ValidateTemporal(TriggerTemporalEvidence? temporal)
    {
        if (temporal is null)
        {
            return [Error("required", "temporal")];
        }

        var errors = new List<TriggerContractError>();
        var values = new (string Field, DateTimeOffset? Value)[]
        {
            ("temporal.createdAtUtc", temporal.CreatedAtUtc),
            ("temporal.observedAtUtc", temporal.ObservedAtUtc),
            ("temporal.receivedAtUtc", temporal.ReceivedAtUtc),
            ("temporal.admittedAtUtc", temporal.AdmittedAtUtc),
            ("temporal.notBeforeUtc", temporal.NotBeforeUtc),
            ("temporal.deadlineUtc", temporal.DeadlineUtc),
            ("temporal.expiresAtUtc", temporal.ExpiresAtUtc)
        };
        foreach (var (field, value) in values)
        {
            if (value is { } utcValue && utcValue.Offset != TimeSpan.Zero)
            {
                errors.Add(Error("utc_required", field));
            }

            if (value is { } instant && (instant < temporal.CreatedAtUtc || instant - temporal.CreatedAtUtc > TriggerDeliveryLimits.MaxTemporalHorizon))
            {
                errors.Add(Error("temporal_horizon_exceeded", field));
            }
        }

        if (temporal.ObservedAtUtc < temporal.CreatedAtUtc || temporal.ReceivedAtUtc < temporal.ObservedAtUtc || temporal.AdmittedAtUtc < temporal.ReceivedAtUtc)
        {
            errors.Add(Error("reordered_temporal_evidence", "temporal"));
        }

        if (temporal.AdmittedAtUtc is { } admittedBeforeEligibility && temporal.NotBeforeUtc is { } eligibilityStart && admittedBeforeEligibility < eligibilityStart)
        {
            errors.Add(Error("admission_before_not_before", "temporal.admittedAtUtc"));
        }

        if (temporal.AdmittedAtUtc is { } admittedAfterDeadline && temporal.DeadlineUtc is { } deadlineEnd && admittedAfterDeadline > deadlineEnd)
        {
            errors.Add(Error("admission_after_deadline", "temporal.admittedAtUtc"));
        }

        if (temporal.AdmittedAtUtc is { } admittedAtExpiry && temporal.ExpiresAtUtc is { } expiryEnd && admittedAtExpiry >= expiryEnd)
        {
            errors.Add(Error("admission_at_or_after_expiry", "temporal.admittedAtUtc"));
        }

        if (temporal.DeadlineUtc is { } deadline && temporal.NotBeforeUtc is { } notBefore && deadline < notBefore)
        {
            errors.Add(Error("deadline_before_not_before", "temporal.deadlineUtc"));
        }

        if (temporal.ExpiresAtUtc is { } expiry && temporal.NotBeforeUtc is { } eligibility && expiry <= eligibility)
        {
            errors.Add(Error("expiry_not_after_not_before", "temporal.expiresAtUtc"));
        }

        if (temporal.ExpiresAtUtc is { } expires && temporal.DeadlineUtc is { } deadlineInstant && expires < deadlineInstant)
        {
            errors.Add(Error("expiry_before_deadline", "temporal.expiresAtUtc"));
        }

        return errors;
    }

    /// <summary>
    /// Revalidates an exact adapter capability and implementation reference.
    /// </summary>
    /// <param name="adapter">The candidate adapter reference.</param>
    /// <returns>The structured validation result.</returns>
    public static TriggerContractValidationResult ValidateAdapterReference(TriggerAdapterReference? adapter)
    {
        if (adapter?.Capability?.Id is null || adapter.Capability.Version is null || adapter.Capability.Hash is null || adapter.Implementation?.ProviderId is null)
        {
            return Result(Error("invalid_adapter_reference", "adapter"));
        }

        if (!CapabilityId.TryParse(adapter.Capability.Id.Value, out _, out _)
            || !CapabilityVersion.TryParse(adapter.Capability.Version.Value, out _, out _)
            || !CapabilityDescriptorHash.TryParse(adapter.Capability.Hash.Value, out _, out _)
            || !CapabilityProviderId.TryParse(adapter.Implementation.ProviderId.Value, out _, out _)
            || !CapabilityIdentifierRules.IsPath(adapter.Implementation.ImplementationId, CapabilityContractLimits.MaxImplementationIdCharacters))
        {
            return Result(Error("invalid_adapter_reference", "adapter"));
        }

        return Result();
    }

    /// <summary>
    /// Revalidates exact profile and boundary-receipt evidence without treating it as an authority grant.
    /// </summary>
    /// <param name="authority">The candidate authority evidence.</param>
    /// <returns>The structured validation result.</returns>
    public static TriggerContractValidationResult ValidateAuthorityEvidence(TriggerAuthorityEvidence? authority)
    {
        if (authority?.Profile?.ProfileId is null || authority.Profile.Revision is null || authority.BoundaryReceipt is null)
        {
            return Result(Error("invalid_authority_evidence", "authority"));
        }

        var validation = AuthorityBoundaryReceiptFactory.Validate(authority.BoundaryReceipt);
        if (!validation.IsValid || !authority.BoundaryReceipt.Profiles.Contains(authority.Profile))
        {
            return Result(Error("invalid_authority_evidence", "authority"));
        }

        return Result();
    }

    private static IReadOnlyList<TriggerContractError> ValidateConversation(bool publicationRequested, Common.Loops.Models.Custom.Execution.CustomLoopConversationReference? conversation, TriggerTemporalEvidence? temporal)
    {
        if (publicationRequested != (conversation is not null))
        {
            return [Error("conversation_reference_required", "invokingConversation")];
        }

        if (conversation is null)
        {
            return [];
        }

        if (!TriggerTextRules.IsToken(conversation.ConversationId, TriggerDeliveryLimits.MaxConversationIdCharacters)
            || !TriggerTextRules.IsSafeNormalized(conversation.CapturedVersion, TriggerDeliveryLimits.MaxConversationVersionCharacters)
            || conversation.CapturedAtUtc.Offset != TimeSpan.Zero
            || temporal is not null && (conversation.CapturedAtUtc < temporal.CreatedAtUtc || conversation.CapturedAtUtc > temporal.ReceivedAtUtc))
        {
            return [Error("invalid_conversation_reference", "invokingConversation")];
        }

        return [];
    }

    private static bool IsStatusReasonPair(TriggerAdmissionStatus status, TriggerAdmissionReason reason)
    {
        return status switch
        {
            TriggerAdmissionStatus.Unknown => reason == TriggerAdmissionReason.Unknown,
            TriggerAdmissionStatus.Admitted => reason == TriggerAdmissionReason.EvidenceAccepted,
            TriggerAdmissionStatus.Replayed => reason == TriggerAdmissionReason.ExactReplay,
            TriggerAdmissionStatus.Conflicting => reason == TriggerAdmissionReason.IdentityConflict,
            TriggerAdmissionStatus.NotYetEligible => reason == TriggerAdmissionReason.NotBefore,
            TriggerAdmissionStatus.Expired => reason is TriggerAdmissionReason.DeadlineExceeded or TriggerAdmissionReason.Expired,
            TriggerAdmissionStatus.Unauthorized => reason is TriggerAdmissionReason.StaleLoop or TriggerAdmissionReason.StaleAdapter or TriggerAdmissionReason.ActorMismatch or TriggerAdmissionReason.SurfaceMismatch or TriggerAdmissionReason.WorkspaceMismatch or TriggerAdmissionReason.RoleMismatch or TriggerAdmissionReason.AuthorityMismatch or TriggerAdmissionReason.StaleAuthority or TriggerAdmissionReason.AuthorityBoundary or TriggerAdmissionReason.StaleDelivery,
            TriggerAdmissionStatus.Unavailable => reason is TriggerAdmissionReason.AdapterUnavailable or TriggerAdmissionReason.HistoryUnavailable,
            TriggerAdmissionStatus.Invalid => reason == TriggerAdmissionReason.InvalidEnvelope,
            _ => false
        };
    }

    private static TriggerContractValidationResult Result(params TriggerContractError[] errors) => new(errors);

    private static TriggerContractError Error(string code, string field) => new(code, field);
}
