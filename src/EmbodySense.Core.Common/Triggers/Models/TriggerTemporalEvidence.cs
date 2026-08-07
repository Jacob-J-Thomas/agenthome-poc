namespace EmbodySense.Core.Common.Triggers.Models;

/// <summary>
/// Captures exact UTC trigger-delivery instants without reading a clock.
/// </summary>
public sealed record TriggerTemporalEvidence
{
    internal TriggerTemporalEvidence(DateTimeOffset observedAtUtc, DateTimeOffset receivedAtUtc, DateTimeOffset createdAtUtc, DateTimeOffset? admittedAtUtc, DateTimeOffset? notBeforeUtc, DateTimeOffset? deadlineUtc, DateTimeOffset? expiresAtUtc)
    {
        ObservedAtUtc = observedAtUtc;
        ReceivedAtUtc = receivedAtUtc;
        CreatedAtUtc = createdAtUtc;
        AdmittedAtUtc = admittedAtUtc;
        NotBeforeUtc = notBeforeUtc;
        DeadlineUtc = deadlineUtc;
        ExpiresAtUtc = expiresAtUtc;
    }

    /// <summary>Gets when the adapter observed the triggering event.</summary>
    public DateTimeOffset ObservedAtUtc { get; }

    /// <summary>Gets when the harness received the delivery.</summary>
    public DateTimeOffset ReceivedAtUtc { get; }

    /// <summary>Gets when the source created the delivery.</summary>
    public DateTimeOffset CreatedAtUtc { get; }

    /// <summary>Gets the optional prior admission instant carried as evidence.</summary>
    public DateTimeOffset? AdmittedAtUtc { get; }

    /// <summary>Gets the optional inclusive eligibility start.</summary>
    public DateTimeOffset? NotBeforeUtc { get; }

    /// <summary>Gets the optional inclusive deadline.</summary>
    public DateTimeOffset? DeadlineUtc { get; }

    /// <summary>Gets the optional exclusive-validity expiry instant.</summary>
    public DateTimeOffset? ExpiresAtUtc { get; }
}
