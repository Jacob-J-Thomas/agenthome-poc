using EmbodySense.Core.Application.Triggers.Models;
using EmbodySense.Core.Common.Triggers;
using EmbodySense.Core.Common.Triggers.Models;

namespace EmbodySense.Core.Application.Triggers;

/// <summary>
/// Creates and validates server-owned terminal admission receipts without granting execution.
/// </summary>
public static class TriggerDeliveryAdmissionReceiptFactory
{
    /// <summary>
    /// Creates a receipt bound to the exact canonical envelope and its stable redelivery semantics.
    /// </summary>
    /// <param name="envelope">The exact envelope whose terminal outcome was recorded.</param>
    /// <param name="status">The terminal admission status.</param>
    /// <param name="reason">The stable terminal reason.</param>
    /// <param name="recordedAtUtc">The exact UTC recording instant.</param>
    /// <param name="receipt">The immutable receipt when successful.</param>
    /// <param name="validation">The structured validation result.</param>
    /// <returns><see langword="true"/> when the envelope, outcome, and time form a valid receipt; otherwise, <see langword="false"/>.</returns>
    public static bool TryCreate(TriggerDeliveryEnvelope? envelope, TriggerAdmissionStatus status, TriggerAdmissionReason reason, DateTimeOffset recordedAtUtc, out TriggerDeliveryAdmissionReceipt? receipt, out TriggerContractValidationResult validation)
    {
        receipt = null;
        var envelopeValidation = TriggerDeliveryValidator.Validate(envelope);
        if (!envelopeValidation.IsValid
            || !TriggerDeliveryHash.TryCompute(envelope, out var envelopeHash, out _)
            || !TriggerDeliveryReplayBindingHash.TryCompute(envelope, out var replayBindingHash))
        {
            validation = new TriggerContractValidationResult(envelopeValidation.IsValid ? [Error("invalid_envelope", "envelope")] : envelopeValidation.Errors);
            return false;
        }

        var candidate = new TriggerDeliveryAdmissionReceipt(TriggerDeliveryAdmissionReceipt.CurrentSchemaVersion, envelope!.DeliveryId, envelope.DeduplicationId, envelopeHash!, replayBindingHash!, status, reason, recordedAtUtc);
        validation = Validate(candidate, envelope);
        if (!validation.IsValid)
        {
            return false;
        }

        receipt = candidate;
        return true;
    }

    /// <summary>
    /// Revalidates a server-sourced receipt against the exact canonical envelope originally classified.
    /// </summary>
    /// <param name="receipt">The candidate receipt.</param>
    /// <param name="envelope">The exact server-owned envelope associated with the receipt.</param>
    /// <returns>The structured validation result.</returns>
    public static TriggerContractValidationResult Validate(TriggerDeliveryAdmissionReceipt? receipt, TriggerDeliveryEnvelope? envelope)
    {
        var errors = new List<TriggerContractError>();
        if (receipt is null)
        {
            return Result(Error("required", "receipt"));
        }

        if (receipt.SchemaVersion != TriggerDeliveryAdmissionReceipt.CurrentSchemaVersion)
        {
            errors.Add(Error("unsupported_schema_version", "receipt.schemaVersion"));
        }

        if (!IsTerminalOutcome(receipt.Status, receipt.Reason))
        {
            errors.Add(Error("invalid_terminal_outcome", "receipt.status"));
        }

        if (receipt.RecordedAtUtc.Offset != TimeSpan.Zero)
        {
            errors.Add(Error("utc_required", "receipt.recordedAtUtc"));
        }

        var envelopeValidation = TriggerDeliveryValidator.Validate(envelope);
        errors.AddRange(envelopeValidation.Errors.Select(error => error with { Field = $"envelope.{error.Field}" }));
        if (envelopeValidation.IsValid)
        {
            var validatedEnvelope = envelope!;
            if (receipt.DeliveryId?.Equals(validatedEnvelope.DeliveryId) != true)
            {
                errors.Add(Error("receipt_identity_mismatch", "receipt.deliveryId"));
            }

            if (receipt.DeduplicationId?.Equals(validatedEnvelope.DeduplicationId) != true)
            {
                errors.Add(Error("receipt_identity_mismatch", "receipt.deduplicationId"));
            }

            if (!TriggerDeliveryHash.TryCompute(validatedEnvelope, out var envelopeHash, out _)
                || !IsSha256(receipt.CanonicalEnvelopeHash)
                || !string.Equals(receipt.CanonicalEnvelopeHash, envelopeHash, StringComparison.Ordinal))
            {
                errors.Add(Error("receipt_envelope_hash_mismatch", "receipt.canonicalEnvelopeHash"));
            }

            if (!TriggerDeliveryReplayBindingHash.TryCompute(validatedEnvelope, out var replayBindingHash)
                || !IsSha256(receipt.ReplayBindingHash)
                || !string.Equals(receipt.ReplayBindingHash, replayBindingHash, StringComparison.Ordinal))
            {
                errors.Add(Error("receipt_replay_binding_mismatch", "receipt.replayBindingHash"));
            }

            if (receipt.RecordedAtUtc < validatedEnvelope.Temporal.ReceivedAtUtc || receipt.RecordedAtUtc - validatedEnvelope.Temporal.CreatedAtUtc > TriggerDeliveryLimits.MaxTemporalHorizon)
            {
                errors.Add(Error("invalid_receipt_time", "receipt.recordedAtUtc"));
            }

            if (receipt.Status is TriggerAdmissionStatus.Admitted or TriggerAdmissionStatus.Replayed)
            {
                if (validatedEnvelope.Temporal.NotBeforeUtc is { } notBeforeUtc && receipt.RecordedAtUtc < notBeforeUtc)
                {
                    errors.Add(Error("receipt_before_not_before", "receipt.recordedAtUtc"));
                }

                if (validatedEnvelope.Temporal.DeadlineUtc is { } deadlineUtc && receipt.RecordedAtUtc > deadlineUtc)
                {
                    errors.Add(Error("receipt_after_deadline", "receipt.recordedAtUtc"));
                }

                if (validatedEnvelope.Temporal.ExpiresAtUtc is { } expiresAtUtc && receipt.RecordedAtUtc >= expiresAtUtc)
                {
                    errors.Add(Error("receipt_at_or_after_expiry", "receipt.recordedAtUtc"));
                }
            }
            else if (receipt.Status == TriggerAdmissionStatus.Expired && receipt.Reason == TriggerAdmissionReason.DeadlineExceeded)
            {
                if (validatedEnvelope.Temporal.DeadlineUtc is not { } deadlineUtc
                    || receipt.RecordedAtUtc <= deadlineUtc
                    || validatedEnvelope.Temporal.ExpiresAtUtc is { } expiresAtUtc && receipt.RecordedAtUtc >= expiresAtUtc)
                {
                    errors.Add(Error("invalid_deadline_outcome_time", "receipt.recordedAtUtc"));
                }
            }
            else if (receipt.Status == TriggerAdmissionStatus.Expired && receipt.Reason == TriggerAdmissionReason.Expired
                && (validatedEnvelope.Temporal.ExpiresAtUtc is not { } expiresAtUtc || receipt.RecordedAtUtc < expiresAtUtc))
            {
                errors.Add(Error("invalid_expiry_outcome_time", "receipt.recordedAtUtc"));
            }
        }

        return new TriggerContractValidationResult(errors);
    }

    private static bool IsTerminalOutcome(TriggerAdmissionStatus status, TriggerAdmissionReason reason)
    {
        return status switch
        {
            TriggerAdmissionStatus.Admitted => reason == TriggerAdmissionReason.EvidenceAccepted,
            TriggerAdmissionStatus.Replayed => reason == TriggerAdmissionReason.ExactReplay,
            TriggerAdmissionStatus.Conflicting => reason == TriggerAdmissionReason.IdentityConflict,
            TriggerAdmissionStatus.Expired => reason is TriggerAdmissionReason.DeadlineExceeded or TriggerAdmissionReason.Expired,
            TriggerAdmissionStatus.Unauthorized => reason is TriggerAdmissionReason.StaleLoop or TriggerAdmissionReason.StaleAdapter or TriggerAdmissionReason.ActorMismatch or TriggerAdmissionReason.SurfaceMismatch or TriggerAdmissionReason.WorkspaceMismatch or TriggerAdmissionReason.RoleMismatch or TriggerAdmissionReason.AuthorityMismatch or TriggerAdmissionReason.StaleAuthority or TriggerAdmissionReason.AuthorityBoundary or TriggerAdmissionReason.StaleDelivery,
            TriggerAdmissionStatus.Invalid => reason == TriggerAdmissionReason.InvalidEnvelope,
            _ => false
        };
    }

    private static bool IsSha256(string? value) => value?.Length == TriggerDeliveryLimits.Sha256HexCharacters && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static TriggerContractValidationResult Result(params TriggerContractError[] errors) => new(errors);

    private static TriggerContractError Error(string code, string field) => new(code, field);
}
