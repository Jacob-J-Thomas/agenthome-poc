using EmbodySense.Core.Common.Triggers.Models;

namespace EmbodySense.Core.Application.Triggers;

/// <summary>
/// Represents a structured, non-executing trigger-delivery admission outcome.
/// </summary>
/// <param name="Status">The closed outcome.</param>
/// <param name="Reason">The stable reason.</param>
/// <param name="CanonicalEnvelopeHash">The exact canonical envelope hash when validation succeeded.</param>
/// <param name="IsReplay">Whether a server-sourced terminal receipt determined the outcome.</param>
/// <param name="OriginalStatus">The receipt's original status when replayed.</param>
/// <param name="OriginalReason">The receipt's original reason when replayed.</param>
public sealed record TriggerDeliveryAdmissionResult(TriggerAdmissionStatus Status, TriggerAdmissionReason Reason, string? CanonicalEnvelopeHash, bool IsReplay = false, TriggerAdmissionStatus? OriginalStatus = null, TriggerAdmissionReason? OriginalReason = null)
{
    /// <summary>Gets a value indicating whether admission evidence was accepted or exactly replayed.</summary>
    public bool IsAdmitted => Status is TriggerAdmissionStatus.Admitted or TriggerAdmissionStatus.Replayed;

    /// <summary>Gets a value that remains false because admission evidence never grants execution.</summary>
    public bool CanExecute => false;
}
