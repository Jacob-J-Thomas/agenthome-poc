using EmbodySense.Core.Common.Triggers;
using EmbodySense.Core.Common.Triggers.Models;

namespace EmbodySense.Core.Application.Triggers.Models;

/// <summary>
/// Records one server-sourced terminal admission outcome bound to the exact admitted envelope and its stable replay semantics.
/// </summary>
/// <remarks>The receipt is durable outcome evidence only and never grants execution, authority, capability, dispatch, or queue access.</remarks>
/// <param name="SchemaVersion">The receipt schema version.</param>
/// <param name="DeliveryId">The exact delivery identity originally classified.</param>
/// <param name="DeduplicationId">The stable deduplication identity.</param>
/// <param name="CanonicalEnvelopeHash">The exact canonical hash of the originally classified envelope.</param>
/// <param name="ReplayBindingHash">The canonical hash of fields that must remain stable across redelivery.</param>
/// <param name="Status">The original terminal admission status.</param>
/// <param name="Reason">The original terminal admission reason.</param>
/// <param name="RecordedAtUtc">The exact UTC instant at which the terminal outcome was recorded.</param>
public sealed record TriggerDeliveryAdmissionReceipt(
    int SchemaVersion,
    TriggerDeliveryId DeliveryId,
    TriggerDeduplicationId DeduplicationId,
    string CanonicalEnvelopeHash,
    string ReplayBindingHash,
    TriggerAdmissionStatus Status,
    TriggerAdmissionReason Reason,
    DateTimeOffset RecordedAtUtc)
{
    /// <summary>Gets the only supported experimental receipt schema version.</summary>
    public const int CurrentSchemaVersion = 1;
}
