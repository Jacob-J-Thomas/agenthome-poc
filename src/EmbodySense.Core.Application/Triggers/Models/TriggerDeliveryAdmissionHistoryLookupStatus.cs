namespace EmbodySense.Core.Application.Triggers.Models;

/// <summary>
/// Identifies whether bounded server-owned trigger admission history could be inspected safely.
/// </summary>
public enum TriggerDeliveryAdmissionHistoryLookupStatus
{
    /// <summary>No supported lookup outcome is present.</summary>
    Unknown = 0,
    /// <summary>History was inspected and contains zero, one, or two independently identified matches.</summary>
    Available = 1,
    /// <summary>History could not be inspected safely.</summary>
    Unavailable = 2
}
