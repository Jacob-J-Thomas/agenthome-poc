namespace EmbodySense.Core.Application.Triggers.Models;

/// <summary>Identifies the closed durable dispatch posture.</summary>
public enum TriggerDispatchOutcome
{
    /// <summary>No dispatch evidence exists.</summary>
    None,

    /// <summary>Intent is durable but the provider outcome is not yet proved.</summary>
    IntentRecorded,

    /// <summary>The governed runner accepted the request.</summary>
    Accepted,

    /// <summary>The synchronous governed runner returned exact durable terminal run evidence.</summary>
    Terminal,

    /// <summary>The request was proved rejected before provider dispatch.</summary>
    Rejected,

    /// <summary>Provider dispatch may have occurred and automatic retry is forbidden.</summary>
    NeedsReview
}
