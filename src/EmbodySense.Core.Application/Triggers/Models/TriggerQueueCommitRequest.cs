using EmbodySense.Core.Common.Triggers.Models;

namespace EmbodySense.Core.Application.Triggers.Models;

/// <summary>Carries application-validated admission evidence to the composition-owned durable queue adapter.</summary>
/// <remarks>Construction is internal so untrusted callers cannot substitute their own admission result.</remarks>
public sealed record TriggerQueueCommitRequest
{
    internal TriggerQueueCommitRequest(TriggerDeliveryEnvelope envelope, TriggerDeliveryAdmissionReceipt? receipt, TriggerAdmissionStatus admissionStatus, TriggerAdmissionReason admissionReason, string canonicalEnvelopeHash, TriggerQueuePriority priority, DateTimeOffset recordedAtUtc)
    {
        Envelope = envelope;
        Receipt = receipt;
        AdmissionStatus = admissionStatus;
        AdmissionReason = admissionReason;
        CanonicalEnvelopeHash = canonicalEnvelopeHash;
        Priority = priority;
        RecordedAtUtc = recordedAtUtc;
    }

    /// <summary>Gets the exact canonical envelope.</summary>
    public TriggerDeliveryEnvelope Envelope { get; }

    /// <summary>Gets the application-created terminal delivery-admission receipt.</summary>
    public TriggerDeliveryAdmissionReceipt? Receipt { get; }

    /// <summary>Gets the exact application-classified delivery status.</summary>
    public TriggerAdmissionStatus AdmissionStatus { get; }

    /// <summary>Gets the exact application-classified delivery reason.</summary>
    public TriggerAdmissionReason AdmissionReason { get; }

    /// <summary>Gets the exact canonical envelope hash computed by delivery admission.</summary>
    public string CanonicalEnvelopeHash { get; }

    /// <summary>Gets the bounded later-selection priority.</summary>
    public TriggerQueuePriority Priority { get; }

    /// <summary>Gets the exact UTC recording instant.</summary>
    public DateTimeOffset RecordedAtUtc { get; }
}
